# SoftPrint multiplataforma

Aplicativo com painel local, API e fila persistente para imprimir texto, PDF, imagem e ESC/POS no Windows e via CUPS no Linux.

## Novidades

- Núcleo compartilhado com hosts Windows 10/11, Windows 7 Legacy e Linux/CUPS
- Configuração por usuário (sem depender de `.env`) e aba **Configurações**
- Passo a passo do processamento + erro com o quê / onde / por quê
- Reimpressão, filtros, export CSV/JSON, tema escuro, sons, inicialização automática
- Webhook com HMAC (`X-SoftPrint-Signature`) e retry; sem URL → `logs/events-yyyy-MM-dd.log`
- Dashboard web em `http://127.0.0.1:5178/dashboard`
- Métricas, busca de impressoras na rede e diagnóstico (`--diagnose` e `/api/diagnose`)

## Abrir e configurar

1. Abra `dist/windows-modern-x64/SoftPrint.exe` (ou o atalho do instalador). O painel e o mecanismo de impressão iniciam juntos. O executável Windows x64 não exige .NET instalado.
2. Escolha uma impressora. Use **Atualizar impressoras** depois de instalar ou conectar um equipamento.
3. Mantenha **Simular** marcado durante a configuração.
4. Clique em **Salvar configurações**. A mensagem **Reconhecida pelo SoftPrint** confirma a aplicação e mostra a versão salva.
5. Clique em **Enviar teste** e acompanhe o resultado no histórico. Dê duplo clique em um pedido para ver detalhes ou erros.
6. Para imprimir de verdade, desmarque a simulação e salve novamente. O envio de teste nesse modo pede confirmação.

As configurações entram em vigor sem reiniciar. Um trabalho já iniciado mantém a configuração capturada no início do envio. A pausa mantém novos pedidos na fila, sem cancelar um envio em andamento. As configurações e o histórico são recuperados ao abrir novamente.

Minimize ou feche o painel para continuar em segundo plano: a fila e a API seguem ativas no ícone da bandeja. Use **Sair** nesse ícone para encerrar. Com **Iniciar com o Windows**, o SoftPrint sobe direto na bandeja. Nas edições Legacy e Linux o processo também permanece em segundo plano depois de fechar o navegador.

## Conectar um sistema

No rodapé do painel, use **Copiar endereço** e **Copiar chave**. O sistema precisa oferecer integração por API ou ser adaptado para fazer as chamadas abaixo.

Endereço padrão: `http://127.0.0.1:5178`. O acesso desta versão é local, no mesmo computador. Todas as rotas exigem o cabeçalho `X-SoftPrint-Key`. Durante a transição, `X-AutoPrint-Key` continua aceito.

A chave fica em `%LocalAppData%\SoftPrint\data\api-key.txt` no Windows e em `~/.local/share/softprint/api-key.txt` no Linux.

```powershell
$key = (Get-Content "$env:LOCALAPPDATA\SoftPrint\data\api-key.txt" -Raw).Trim()
$headers = @{ 'X-SoftPrint-Key' = $key }
$body = @{ reference = 'pedido-001'; text = "PEDIDO 001`nProduto: exemplo`nQuantidade: 1" } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:5178/api/jobs -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
Invoke-RestMethod http://127.0.0.1:5178/api/jobs -Headers $headers
```

Uma referência repetida retorna o trabalho existente, mesmo se o texto mudar. Para reimprimir deliberadamente, confira o resultado anterior e envie uma nova referência.

| Rota | Uso |
| --- | --- |
| `GET /api/status` | Estado, saúde, fila, flags, versão e atualização |
| `GET /api/update` | Checagem de versão no GitHub Releases |
| `GET /api/diagnose` | Diagnóstico de SO, runtime, backend e recursos |
| `GET /api/printers` | Impressoras instaladas |
| `GET /api/templates` | Templates |
| `GET /api/settings` | Configuração e versão atuais |
| `PUT /api/settings` | Salvar e aplicar sem reiniciar |
| `GET /api/system-settings` | Opções deste computador (segredos mascarados) |
| `PUT /api/system-settings` | Salvar opções deste computador |
| `POST /api/startup` | Ligar/desligar inicialização automática |
| `POST /api/jobs` | Receber pedido (`text`/`pdf`/`image`/`escpos`) |
| `POST /api/jobs/{id}/reprint` | Reimprimir com nova referência |
| `GET /api/jobs` | Histórico completo |
| `GET /api/jobs/{id}` | Consultar um pedido |

Para configurar por outro painel ou sistema, leia `/api/settings` e envie para `PUT /api/settings`:

```json
{
  "printerName": "NOME EXATO DA IMPRESSORA",
  "simulation": true,
  "paused": false,
  "expectedRevision": 1
}
```

Use em `expectedRevision` a versão retornada pela consulta. HTTP 409 significa que outra alteração foi salva nesse intervalo: recarregue antes de tentar novamente. O painel consulta o estado a cada dois segundos e preserva alterações locais ainda não salvas, avisando se houver conflito.

## Compatibilidade

- Windows 10/11: `SoftPrint.exe`, painel WebView2 integrado (se o runtime faltar, o painel abre no navegador) e spooler do Windows.
- Windows 7 SP1: `SoftPrint.Legacy.exe`, painel aberto no navegador e runtime .NET 6 congelado. Essa edição funciona como compatibilidade, mas Windows 7 e .NET 6 não recebem atualizações de segurança da Microsoft.
- Linux x64: executável `softprint`, painel no navegador, catálogo via `lpstat`/`lpoptions` e impressão via `lp`. ESC/POS usa CUPS raw ou TCP 9100 (`tcp:IP:9100`).

Execute `SoftPrint.exe --diagnose` (ou `softprint --diagnose`) para conferir sistema, arquitetura e backend. O endpoint `/api/status` informa as capacidades para o painel ocultar recursos indisponíveis. A aba Configurações mostra o diagnóstico completo.

## Arquivos e estados

As configurações de cada usuário ficam em `%LocalAppData%\SoftPrint` no Windows e em `~/.config/softprint` / `~/.local/share/softprint` no Linux. Dados antigos ao lado do executável e de instalações AutoPrint são migrados automaticamente. O `.env` é apenas um override administrativo opcional; o programa inicia com defaults seguros sem esse arquivo.

Estados dos pedidos: `pending` (na fila), `processing` (enviando), `simulated` (simulado), `sent` (enviado ao spooler/CUPS), `uncertain` (exige conferência). `sent` confirma entrega ao sistema de impressão, não a saída física do papel. Não há reenvio automático de trabalhos incertos, para evitar duplicatas. O histórico inclui a impressora e a versão de configuração usadas.

O suporte atual é texto, PDF, imagem e ESC/POS. Papel, orientação, encaixe e escala são os salvos no painel: o preview e o envio usam a mesma área imprimível do driver (sem a margem extra de 1" do Windows). Integração com sistemas específicos depende do equipamento e do sistema escolhido.

## Compilar e testar

Para desenvolvimento, instale o SDK .NET 10 (e o SDK .NET 6 se for publicar a edição Legacy):

```powershell
dotnet build .\SoftPrint.slnx
dotnet test .\SoftPrint.Tests\SoftPrint.Tests.csproj -f net10.0
.\scripts\publish-all.ps1
.\Test-SoftPrint.ps1
```

Use `SoftPrint.exe --headless true` para iniciar somente a API, sem painel nem bandeja. `SoftPrint.exe --tray` inicia em segundo plano, com o ícone na bandeja. O teste automatizado cria uma cópia isolada e verifica autenticação, fila simulada, deduplicação, configuração dinâmica, validação da impressora, conflitos, pausa, retomada e persistência após reiniciar. Não envia nada para impressão física.

Pacote Linux, depois do publish:

```sh
./packaging/linux/install.sh
```

## Versionamento e atualizações (GitHub Releases)

A cada **push na `main`**, o GitHub Actions:

1. Incrementa automaticamente o patch (`1.0.2` → `1.0.3`)
2. Atualiza `VERSION`, `SoftPrintVersion.cs` e `installer/SoftPrint.iss`
3. Publica os builds Windows/Linux e gera `SoftPrint-Setup.exe`
4. Cria a tag `vX.Y.Z` e o **GitHub Release** com o instalador

Os clientes instalados consultam o release mais recente e mostram o aviso no painel (com download/instalação automática no Windows).

### Manual

- **Actions → Release → Run workflow**: escolha `patch`, `minor` ou `major`
- Marque **mandatory** para release obrigatória (`[mandatory]` nas notas)
- Para não gerar release num commit: inclua `[skip release]` na mensagem

Arquivos de versão (atualizados pelo CI):

1. `VERSION`
2. `SoftPrint.Domain/SoftPrintVersion.cs`
3. `installer/SoftPrint.iss` (`#define AppVersion`)

API:

| Rota | Uso |
| --- | --- |
| `GET /api/update` | Versão atual, última do GitHub, URL de download e se é obrigatória |
| `GET /api/status` | Inclui `version` e `update` (cache) |

Releases públicos não precisam de token. Para repositório privado, configure o secret `SOFTPRINT_UPDATE_TOKEN` no GitHub Actions (é injetado nos builds) ou `UPDATE_GITHUB_TOKEN` no `.env` local.

Atualização no Windows: o cliente prefere o zip **delta** (`SoftPrint-win-x64-from-1.0.8.zip`, etc.) com patch binário do `.exe` + só arquivos alterados; se não houver delta para a versão instalada, usa o zip completo; por último o `SoftPrint-Setup.exe`. Pacotes de update não incluem `.pdb`/`.xml`.

Assinatura Authenticode (opcional no CI): secrets `CODE_SIGNING_PFX_BASE64` e `CODE_SIGNING_PASSWORD`.

Telemetria de falhas: opt-in no painel (Sistema) ou `TELEMETRY_ENABLED` / `TELEMETRY_URL` — envia só metadados anônimos (versão, plataforma, tipo, erro), sem conteúdo do pedido.

## Checklist de testes externos

Esta máquina de desenvolvimento é Windows 10. Os itens abaixo **não** podem ser comprovados aqui e precisam de validação no hardware real.

### Windows 7 SP1 (x64 e, se possível, x86)

- [ ] Instalar pelo `SoftPrint-Setup.exe` e confirmar que a edição Legacy é escolhida automaticamente
- [ ] Abrir `SoftPrint.Legacy.exe`; o painel deve abrir no navegador em `http://127.0.0.1:5178/dashboard`
- [ ] Confirmar o aviso de edição sem suporte de segurança
- [ ] Listar impressoras USB/GDI, enviar teste simulado e um texto real
- [ ] Conferir PDF/imagem/ESC-POS no equipamento disponível
- [ ] Migrar dados de uma pasta AutoPrint antiga (`jobs.json`, `settings.json`, `api-key.txt`)
- [ ] Executar `SoftPrint.Legacy.exe --diagnose`

### Linux x64 com CUPS

- [ ] Instalar `cups-client` e executar `packaging/linux/install.sh`
- [ ] Abrir o painel no navegador e listar filas `lpstat -a`
- [ ] Imprimir texto/PDF via `lp` em simulação e depois em modo real
- [ ] ESC/POS por fila CUPS raw e por `tcp:IP:9100`
- [ ] Habilitar/desabilitar `systemctl --user enable --now softprint.service`
- [ ] Executar `softprint --diagnose` e conferir `/api/status` (`platform=linux`, `printingBackend=cups`)
