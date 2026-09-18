# AutoPrint para Windows

Aplicativo com painel visual integrado, API local e fila persistente para imprimir texto, PDF, imagem e ESC/POS usando os drivers do Windows.

## Novidades

- Passo a passo do processamento + erro com o quê / onde / por quê
- `.env` para configs e tipos de status
- Reimpressão, filtros, export CSV/JSON, tema escuro, sons, iniciar com o Windows
- Webhook com HMAC (`WEBHOOK_SECRET`) e retry; sem URL → `logs/events-yyyy-MM-dd.log`
- Dashboard web em `http://127.0.0.1:5178/dashboard`
- Métricas (jobs/h, taxa de uncertain), busca de impressoras na rede
- Roteamento por tipo, templates, PDF/imagem/ESC-POS

## Abrir e configurar

1. Abra `release/AutoPrint/AutoPrint.exe`. O painel e o mecanismo de impressão iniciam juntos. O executável funciona no Windows x64 sem instalar .NET.
2. Escolha uma impressora instalada no Windows. Use **Atualizar impressoras** depois de instalar ou conectar um equipamento.
3. Mantenha **Simular sem gastar papel** marcado durante a configuração.
4. Clique em **Salvar configurações**. A mensagem **Reconhecida pelo AutoPrint** confirma a aplicação e mostra a versão salva.
5. Clique em **Enviar teste** e acompanhe o resultado no histórico. Dê duplo clique em um pedido para ver detalhes ou erros.
6. Para imprimir de verdade, desmarque a simulação e salve novamente. O envio de teste nesse modo pede confirmação.

As configurações entram em vigor sem reiniciar. Um trabalho já iniciado mantém a configuração capturada no início do envio. A pausa mantém novos pedidos na fila, sem cancelar um envio em andamento. As configurações e o histórico são recuperados ao abrir novamente.

Minimize a janela para continuar usando outros programas. **Fechar o painel encerra também o AutoPrint.** Uma segunda execução da mesma pasta é bloqueada para proteger os dados.

## Conectar um sistema

No rodapé do painel, use **Copiar endereço** e **Copiar chave**. O sistema precisa oferecer integração por API ou ser adaptado para fazer as chamadas abaixo. Copiar esses dados não conecta automaticamente sistemas comerciais que não suportam essa integração.

Endereço padrão: `http://127.0.0.1:5178`. O acesso desta versão é local, no mesmo computador. Todas as rotas exigem o cabeçalho `X-AutoPrint-Key`.

Na raiz do projeto, com o executável aberto:

```powershell
$autoPrintKey = (Get-Content .\release\AutoPrint\data\api-key.txt -Raw).Trim()
$autoPrintHeaders = @{ 'X-AutoPrint-Key' = $autoPrintKey }
$autoPrintBody = @{ reference = 'pedido-001'; text = "PEDIDO 001`nProduto: exemplo`nQuantidade: 1" } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:5178/api/jobs -Method Post -Headers $autoPrintHeaders -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($autoPrintBody))
Invoke-RestMethod http://127.0.0.1:5178/api/jobs -Headers $autoPrintHeaders
```

Uma referência repetida retorna o trabalho existente, mesmo se o texto mudar. Para reimprimir deliberadamente, confira o resultado anterior e envie uma nova referência.

| Rota | Uso |
| --- | --- |
| `GET /api/status` | Estado, saúde, fila e flags |
| `GET /api/printers` | Impressoras instaladas no Windows |
| `GET /api/templates` | Templates do `.env` |
| `GET /api/settings` | Configuração e versão atuais |
| `PUT /api/settings` | Salvar e aplicar sem reiniciar |
| `POST /api/startup` | Ligar/desligar início com o Windows |
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

## Arquivos e estados

Mantenha o executável e `appsettings.json` juntos. O aplicativo cria `data/api-key.txt`, `data/jobs.json` e, no primeiro salvamento, `data/settings.json`, ao lado do executável. Preserve essa pasta para manter chave, histórico e configurações. A configuração salva no painel tem prioridade sobre os valores iniciais de `appsettings.json`.

Estados dos pedidos: `pending` (na fila), `processing` (enviando), `simulated` (simulado), `sent` (enviado ao Windows), `uncertain` (exige conferência). `sent` confirma entrega ao spooler, não a saída física do papel. Não há reenvio automático de trabalhos incertos, para evitar duplicatas. O histórico inclui a impressora e a versão de configuração usadas.

O suporte atual é texto, com papel e margens definidos no driver. PDF, etiquetas, protocolos de impressoras térmicas e integração com sistemas específicos dependem de implementação conforme o equipamento e o sistema escolhido. Ainda não inclui serviço do Windows ou inicialização automática.

## Compilar e testar

Para desenvolvimento, instale o SDK .NET 10 para Windows:

```powershell
dotnet publish .\AutoPrint -p:PublishProfile=Windows -o .\release\AutoPrint
.\Test-AutoPrint.ps1
```

Use `AutoPrint.exe --headless true` para iniciar somente a API, sem painel. O teste automatizado cria uma cópia isolada e verifica autenticação, fila simulada, deduplicação, configuração dinâmica, validação da impressora, conflitos, pausa, retomada e persistência após reiniciar. Não envia nada para impressão física.
