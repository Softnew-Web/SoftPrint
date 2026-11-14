# SoftPrint — tempo real e comandos remotos (etapa 2)

Data: 2026-11-14 · Repositórios: SoftPrint, backendempresa, conex_database · Hub: servidor_websocket (sem mudanças)

## Objetivo

O painel da Softnew passa a **agir** nas máquinas com SoftPrint, não só observar: atualizar, pausar e retomar a
fila, reimprimir os trabalhos com erro, fazer impressão de teste, pedir o pacote de suporte e pedir o status na hora.
O comando chega em segundos pelo WebSocket; sem WebSocket, chega pelo heartbeat. O status do SoftPrint também
passa a chegar ao painel quando muda, sem esperar o intervalo do heartbeat.

Fora do escopo desta etapa: alertas de fila travada e lista de impressoras (etapa 3), versões na frota e
"atualizar todas" (etapa 4), relatório de disponibilidade e resumo da manhã (etapa 5), cancelar trabalho e
voltar versão pelo painel.

## Decisões

1. **WebSocket só como aviso.** A mensagem do hub diz "há comando novo"; o SoftPrint busca os comandos no backend
   por HTTPS com o token da empresa e devolve o resultado por HTTPS. O backend é a única fonte do que foi pedido.
   Uma mensagem do hub nunca executa nada sozinha.
2. **Heartbeat continua** e é a reserva: a resposta dele informa quantos comandos estão pendentes. Sem WebSocket
   conectado, o SoftPrint manda heartbeat a cada 15 s (em vez de `FleetHeartbeatSeconds`, padrão 40 s).
3. **Hub sem mudanças.** O `servidor_websocket` já aceita conexão sem `Origin`, token no header `Authorization`
   e token limitado a salas (`allowed_rooms`).
4. **Lista fechada de comandos.** Tipo desconhecido volta como falha "comando desconhecido".
5. **Cada comando roda uma vez.** O backend só entrega comandos `pending`, e marca `delivered` na mesma transação.
   O SoftPrint ainda guarda em memória os ids já executados, para o caso de a mesma resposta chegar duas vezes.

## Backend (backendempresa)

### Tabela `SOFTPRINT_COMMANDS` (criada em `SoftPrintSchema`, como as outras)

| Coluna | Tipo | Observação |
|---|---|---|
| `id` | BIGINT UNSIGNED PK AUTO_INCREMENT | |
| `emp_codigo` | INT | |
| `machine_pk` | BIGINT UNSIGNED | `SOFTPRINT_MACHINES.id` |
| `type` | VARCHAR(32) | ver lista abaixo |
| `params` | TEXT NULL | JSON; vazio na v1 |
| `status` | VARCHAR(16) | `pending`, `delivered`, `done`, `failed`, `expired` |
| `requested_by` | VARCHAR(80) | usuário do painel |
| `created_at` | DATETIME | |
| `delivered_at` | DATETIME NULL | |
| `finished_at` | DATETIME NULL | |
| `result_message` | VARCHAR(500) NULL | texto que o SoftPrint devolveu |
| `result_data` | TEXT NULL | JSON opcional |

Índices: `(machine_pk, status)`, `(emp_codigo, created_at)`.

Tipos aceitos: `update`, `pause`, `resume`, `reprint_failed`, `test_print`, `support_package`, `refresh`.

Regras:

- Criar comando para máquina inexistente → 404. Tipo fora da lista → 422.
- Máquina com o mesmo tipo já `pending` ou `delivered` → devolve o comando existente (não duplica).
- O cron (`runCron`) marca `expired` o que estiver `pending` há mais de `SOFTPRINT_COMMAND_TTL_SECONDS`
  (padrão 600) e `failed` ("sem resposta do SoftPrint") o que estiver `delivered` há mais de 15 min.
- O `update` fica `delivered` enquanto o SoftPrint baixa e reinicia; o resultado é enviado pela versão nova ao subir,
  ou pela antiga se falhar.
- Histórico guardado por 90 dias (cron apaga os mais antigos).

### Coluna nova em `SOFTPRINT_MACHINES`

`paused TINYINT(1) NOT NULL DEFAULT 0`, gravada pelo heartbeat (campo `paused`). Como a tabela já existe em
produção, `SoftPrintSchema` ganha um passo de `ALTER TABLE … ADD COLUMN` que ignora o erro de coluna duplicada,
e a versão do marcador de schema sobe para `v2`.

### Rotas do painel (grupo `/api/softprint`, já restrito a perfil administrativo na etapa 1)

- `POST /api/softprint/machines/{id}/commands` — corpo `{ "type": "pause" }`. Resposta 201 com o comando.
  Publica `softprint.command` na sala `softprint:m:{id}` (payload `{ "id": <comando> }`) e `softprint.updated`
  em `monitor:all`.
- `GET /api/softprint/machines/{id}/commands?limit=` — histórico, mais recentes primeiro (máx. 200).
- `GET /api/softprint/machines` passa a trazer `pending_commands` (quantos `pending` + `delivered`).

### Rotas do agente (grupo `/api/softprint/agent`, token da empresa)

- `GET /api/softprint/agent/realtime-token?machineId=` — resolve a máquina da empresa pelo `machineId`
  (404 se não existe: o SoftPrint precisa ter mandado um heartbeat antes). Responde
  `{ ws_url, token, room, expires_at }`. O JWT é emitido com `RealtimeJwt`: `sub = softprint:{machine_pk}`,
  `project` = o do painel, `scopes = ["realtime.subscribe"]`, `allowed_rooms = ["softprint:m:{machine_pk}"]`,
  validade de 12 h. `ws_url` vem de `REALTIME_BASE_URL` trocando `http`→`ws` / `https`→`wss` e somando `/v1/ws`.
  Sem `REALTIME_JWT_SECRET` → 503 (o SoftPrint fica só no heartbeat).
- `GET /api/softprint/agent/commands?machineId=` — devolve os `pending` da máquina em ordem de criação e os marca
  `delivered` na mesma transação (`SELECT … FOR UPDATE`).
- `POST /api/softprint/agent/commands/{id}/result` — corpo `{ machineId, ok, message, data }`. Só aceita comando
  da própria máquina e da própria empresa (senão 404). Grava `done`/`failed`, `finished_at`, mensagem (até 500
  caracteres) e `data` (JSON até 8 KB). Repetir o resultado de um comando já finalizado → 200 sem mudar nada.
  Publica `softprint.updated`.
- Resposta do `POST /api/softprint/agent/heartbeat` ganha `pending_commands` (número).

## SoftPrint

### `FleetRealtimeClient` (BackgroundService, novo)

- Só roda com token configurado e depois de um heartbeat com sucesso (para a máquina existir no backend).
- Pede o token em `/agent/realtime-token`, conecta com `ClientWebSocket` em `ws_url` com
  `Authorization: Bearer <token>` e envia `{"action":"subscribe","rooms":["softprint:m:<id>"]}`.
- Mensagem com `type == "softprint.command"` → pede ao `FleetCommandRunner` para buscar os comandos.
- Ao conectar (e a cada reconexão) também busca os comandos, para pegar o que chegou enquanto estava fora.
- Reconecta com espera crescente (2 s, 5 s, 15 s, 30 s, 60 s, com variação aleatória). Renova o token 30 min
  antes de vencer, reconectando.
- Expõe `Connected` para o heartbeat e para a tela Sistema ("tempo real: conectado / usando consulta").
- Token trocado ou apagado na tela Sistema → derruba a conexão e recomeça.

### `FleetCommandRunner` (novo)

- `PollAsync()`: `GET /agent/commands`; executa um por vez, na ordem; manda o resultado de cada um.
- Uma execução por vez (semáforo); chamadas simultâneas (WebSocket + heartbeat) esperam a que está rodando.
- Guarda os últimos 200 ids executados e ignora repetidos.
- Execução de cada tipo (usando o que já existe):

| Tipo | Implementação | Resultado |
|---|---|---|
| `update` | `IUpdateApplier.TryStart`; se `false`, falha com o erro. Grava o id do comando em `fleet.json` (`PendingUpdateCommand`). Ao subir, se a versão mudou, manda `done` "Atualizado para X"; se não mudou ou houve aviso de falha (`UpdateFailureNotice`), manda `failed` com o motivo. | Resultado só depois do reinício |
| `pause` / `resume` | `SettingsService.Update` com `Paused = true` / `false` e a revisão atual; conflito de revisão → tenta de novo uma vez | "Fila pausada" / "Fila retomada" |
| `reprint_failed` | `JobQueueService.Reprint` para cada trabalho `Uncertain` das últimas 24 h, no máximo 50 | "N trabalhos reenviados" |
| `test_print` | mesmo fluxo de `/api/jobs/guided-test` (extraído para um método reutilizável) | id e referência do trabalho |
| `support_package` | `FleetApi.SendSupportBundleAsync` com observação "Pedido pelo painel por <usuário>" | ok ou o erro do envio |
| `refresh` | `SendHeartbeatAsync` | "Status enviado" |

- Exceção na execução → `failed` com a mensagem da exceção (até 500 caracteres).

### Heartbeat

- Lê `pending_commands` da resposta; se for maior que zero, chama `FleetCommandRunner.PollAsync()`.
- Intervalo: `FleetHeartbeatSeconds` com o WebSocket conectado; 15 s sem ele.
- **Status na hora:** `JobQueueService` e `SettingsService` avisam mudança (evento novo `Changed`); o
  `FleetHeartbeatHostedService` manda um heartbeat 2 s depois da última mudança, no máximo um a cada 5 s.

### Tela Sistema → Monitoramento Softnew

Mostra "Tempo real: conectado" ou "Tempo real: usando consulta a cada 15 s" e o último comando recebido.

### Windows 7 (Legacy, .NET Core 3.1)

O `ClientWebSocket` do .NET Core 3.1 não deixa escolher a versão do TLS. Se não conectar, o SoftPrint fica na
consulta a cada 15 s; os comandos funcionam com atraso de até 15 s. A conexão será testada no build Legacy em
Windows 10; no Windows 7 real, não.

## Painel (conex_database)

- Na lista de máquinas, botão **Ações** com: Atualizar, Pausar fila / Retomar fila (conforme o estado),
  Reimprimir com erro, Impressão de teste, Pedir pacote de suporte, Atualizar status.
  Atualizar e Pausar pedem confirmação. Com a máquina `offline` as ações continuam disponíveis, com o aviso
  "o comando será entregue quando ela voltar, se for em até 10 min".
- Depois de enviar, a linha mostra o comando: "enviado" → "entregue" → "concluído"/"falhou" com a mensagem.
  Atualiza pelo `softprint.updated`; sem WebSocket no painel, pelo polling de 60 s que já existe.
- Gaveta **Histórico de comandos** por máquina: tipo, quem pediu, quando, situação e mensagem.
- A máquina passa a mostrar se a fila está pausada (o heartbeat ganha o campo `paused`).

## Erros e casos de borda

- Máquina desligada: comando `pending` até expirar (10 min) → "expirado" no painel.
- WebSocket fora: comandos pelo heartbeat a cada 15 s.
- Atualização em instalação "para todos os usuários" sem o serviço do Windows: abre o UAC na máquina do cliente;
  recusado ou ignorado → `failed` "Atualização cancelada no PC".
- SoftPrint reinicia no meio de um comando que não é `update`: o comando fica `delivered` e o cron marca `failed`
  "sem resposta do SoftPrint" depois de 15 min.
- Token do hub vencido ou recusado: pede outro; 3 recusas seguidas → espera 5 min antes de tentar de novo.
- Hub fora do ar: espera crescente até 60 s; nada disso afeta a impressão.

## Testes

- SoftPrint (xUnit, net10 e net6):
  - `FleetCommandRunner`: cada tipo, tipo desconhecido, id repetido ignorado, execução um por vez, resultado do
    `update` depois do reinício (sucesso e falha).
  - `FleetRealtimeClient` contra um servidor WebSocket de teste (Kestrel): assina a sala certa, mensagem
    `softprint.command` dispara a busca, reconecta quando o servidor cai, token renovado.
  - Heartbeat: `pending_commands > 0` dispara a busca; intervalo de 15 s sem WebSocket; status na hora com a
    regra de 2 s / 5 s.
- Backend: `scripts/test_softprint_commands.php` para validação de tipo, ciclo de vida e expiração (funções puras);
  ponta a ponta num MariaDB descartável com o SoftPrint real (Debug) e o hub local em Docker.
- Painel: `npm run build`, `npm test` e conferência da tela.
- Sem teste: WebSocket no Windows 7 real e o UAC numa atualização real.

## Entrega

Um PR por repositório. Ordem de merge: backendempresa (tabela e rotas; o SoftPrint antigo ignora os campos novos),
depois conex_database, depois SoftPrint e release 1.0.42.
