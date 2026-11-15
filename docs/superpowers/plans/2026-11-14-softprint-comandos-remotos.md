# SoftPrint — tempo real e comandos remotos — Plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** O painel da Softnew manda comandos (atualizar, pausar, retomar, reimprimir com erro, impressão de teste, pacote de suporte, status) para cada SoftPrint e vê o resultado e o status da máquina em segundos.

**Architecture:** O backend grava os comandos em `SOFTPRINT_COMMANDS` e avisa a máquina pela sala `softprint:m:<id>` do `servidor_websocket`. O SoftPrint, ao receber o aviso (ou ver `pending_commands` na resposta do heartbeat), busca os comandos por HTTPS, executa e devolve o resultado por HTTPS. Sem WebSocket o heartbeat roda a cada 15 s. O status do SoftPrint sobe na hora quando a fila muda.

**Tech Stack:** PHP 8.2 + Slim 4 + MariaDB (backendempresa) · .NET 10 / 6 / Core 3.1, xUnit, `ClientWebSocket`, `Microsoft.AspNetCore.TestHost` (SoftPrint) · Vue 3 + Pinia + Vite (conex_database) · hub Go `servidor_websocket` (sem mudanças).

Spec: `docs/superpowers/specs/2026-11-14-softprint-comandos-remotos-design.md`.

**Diferença em relação à spec:** "status na hora" não usa um evento novo em `JobQueueService`/`SettingsService`. O `FleetHeartbeatHostedService` olha a cada 2 s uma "impressão digital" da fila (pendentes, enviando, último erro, pausado) e manda heartbeat quando ela muda — mesmo efeito (até ~2–5 s), sem mexer nas camadas de domínio.

---

## Estrutura de arquivos

### backendempresa (branch `feat/softprint-comandos`, a partir de `master`)

| Arquivo | Responsabilidade |
|---|---|
| `src/Utils/SoftPrintSchema.php` (modificar) | DDL de `SOFTPRINT_COMMANDS`, coluna `paused`, marcador `v2` |
| `src/Services/SoftPrintCommandService.php` (criar) | Ciclo de vida dos comandos (criar, entregar, resultado, expirar, listar) |
| `src/Utils/SoftPrintCommandRules.php` (criar) | Regras puras: tipos aceitos, URL do WebSocket, limites de texto |
| `src/Utils/RealtimeJwt.php` (modificar) | `issueForSoftprintMachine()` com `allowed_rooms` |
| `src/Services/RealtimeHub.php` (modificar) | `publishToRoom()` |
| `src/Services/SoftPrintFleetService.php` (modificar) | `paused` no heartbeat, `paused`/`pending_commands` na lista, limpeza no cron |
| `src/Controllers/SoftPrintController.php` (modificar) | Rotas do painel para comandos |
| `src/Controllers/SoftPrintAgentController.php` (modificar) | Rotas do agente: token do hub, comandos, resultado |
| `routes/softprint.php` (modificar) | Registro das rotas |
| `scripts/test_softprint_commands.php` (criar) | Testes das regras puras |
| `.github/workflows/ci.yml` (modificar) | Roda o teste novo |
| `docs/softprint-frota.md` (modificar) | Documentação das rotas novas |

### SoftPrint (branch `feat/comandos-remotos`, já criado com a spec)

| Arquivo | Responsabilidade |
|---|---|
| `SoftPrint.Application/Abstractions/IFleetService.cs` (modificar) | Tipos novos e métodos de comandos/tempo real |
| `SoftPrint.Application/Services/GuidedTestPrint.cs` (criar) | Texto e envio da impressão de teste (usado pela API, Legacy e comandos) |
| `SoftPrint.Infrastructure.Core/Integrations/FleetService.cs` (modificar) | HTTP de comandos, token do hub, `paused`, `pending_commands`, update pendente |
| `SoftPrint.Infrastructure.Core/Integrations/FleetCommandRunner.cs` (criar) | Executa comandos, uma vez cada, um por vez |
| `SoftPrint.Infrastructure.Core/Integrations/FleetRealtimeClient.cs` (criar) | WebSocket com o hub, reconexão, renovação do token |
| `SoftPrint.Infrastructure.Core/Operations/FleetHeartbeatHostedService.cs` (modificar) | Intervalo 15 s sem WS, status na hora, dispara busca de comandos |
| `SoftPrint.Infrastructure.Core/Updates/LocalPreviousVersionStore.cs` (modificar) | `UpdateFailureNotice.Peek()` |
| `SoftPrint.Infrastructure.Core/Hosting/SoftPrintHostExtensions.cs` (modificar) | Registro no DI |
| `SoftPrint.Api/Endpoints/AppEndpoints.cs` (modificar) | Teste guiado usa `GuidedTestPrint` |
| `SoftPrint.Host.Windows.Legacy/Controllers/AppController.cs` (modificar) | Idem |
| `SoftPrint.Host.Windows/wwwroot/js/components/system-settings-tab.js` (modificar) | Mostra "tempo real" e último comando |
| `SoftPrint.Tests/FleetCommandRunnerTests.cs` (criar) | Testes do executor |
| `SoftPrint.Tests/FleetRealtimeClientTests.cs` (criar) | Testes do cliente WS contra TestServer |
| `SoftPrint.Tests/FleetServiceTests.cs` (modificar) | Testes de HTTP novos e regra do heartbeat |

### conex_database (branch `feat/softprint-comandos`, a partir de `main`)

| Arquivo | Responsabilidade |
|---|---|
| `src/stores/softprint.js` (modificar) | `sendCommand`, `fetchCommands`, rótulos |
| `src/components/SoftPrintCommandMenu.vue` (criar) | Botão "Ações" de uma máquina |
| `src/components/SoftPrintCommandHistory.vue` (criar) | Gaveta com o histórico de comandos |
| `src/components/SoftPrintView.vue` (modificar) | Usa os dois componentes, mostra "pausada" e comandos pendentes |
| `src/utils/softprintCommands.test.js` (criar) | Testes dos rótulos e da regra de confirmação (vitest) |

---

## Parte A — backendempresa

Trabalhe em `C:\Users\pc\Desktop\mednews\mednews_web\backendempresa`.

### Task A1: Branch e regras puras dos comandos

**Files:**
- Create: `src/Utils/SoftPrintCommandRules.php`
- Create: `scripts/test_softprint_commands.php`

- [ ] **Step 1: Criar o branch**

```bash
cd /c/Users/pc/Desktop/mednews/mednews_web/backendempresa
git checkout master && git pull --ff-only origin master
git checkout -b feat/softprint-comandos
```

- [ ] **Step 2: Escrever o teste que falha**

`scripts/test_softprint_commands.php`:

```php
<?php
/**
 * Regras dos comandos remotos do SoftPrint (sem banco). Uso: php scripts/test_softprint_commands.php
 */
require __DIR__ . '/../vendor/autoload.php';

use App\Utils\SoftPrintCommandRules as R;

$failures = 0;
function check(string $name, bool $ok): void
{
    global $failures;
    echo ($ok ? '  ok  ' : 'FALHOU') . "  $name\n";
    if (!$ok) $failures++;
}

check('tipos aceitos', R::TYPES === ['update', 'pause', 'resume', 'reprint_failed', 'test_print', 'support_package', 'refresh']);
check('normaliza caixa e espaços', R::normalizeType('  PAUSE ') === 'pause');
check('tipo desconhecido vira null', R::normalizeType('format_c') === null);
check('tipo vazio vira null', R::normalizeType('') === null);

check('https vira wss + /v1/ws', R::wsUrl('https://hub.softnew.com.br') === 'wss://hub.softnew.com.br/v1/ws');
check('http vira ws e tira barra', R::wsUrl('http://127.0.0.1:8080/') === 'ws://127.0.0.1:8080/v1/ws');
check('base vazia vira null', R::wsUrl('') === null);
check('base sem esquema vira null', R::wsUrl('hub.softnew.com.br') === null);

check('sala da máquina', R::room(42) === 'softprint:m:42');

check('mensagem cortada em 500', mb_strlen(R::clipMessage(str_repeat('á', 600))) === 500);
check('mensagem sem controle', R::clipMessage("a\x00b\nc") === 'a b c');
check('data até 8 KB passa', R::encodeData(['x' => str_repeat('a', 100)]) !== null);
check('data acima de 8 KB é descartada', R::encodeData(['x' => str_repeat('a', 9000)]) === null);
check('data não-array é descartada', R::encodeData('texto') === null);

echo $failures === 0 ? "\nTodos os testes passaram.\n" : "\n$failures teste(s) falharam.\n";
exit($failures === 0 ? 0 : 1);
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `php scripts/test_softprint_commands.php`
Expected: `PHP Fatal error: Uncaught Error: Class "App\Utils\SoftPrintCommandRules" not found`

- [ ] **Step 4: Implementar**

`src/Utils/SoftPrintCommandRules.php`:

```php
<?php
namespace App\Utils;

/** Regras puras dos comandos remotos do SoftPrint (sem banco, testáveis isoladamente). */
final class SoftPrintCommandRules
{
    public const TYPES = ['update', 'pause', 'resume', 'reprint_failed', 'test_print', 'support_package', 'refresh'];

    public const MAX_MESSAGE = 500;
    public const MAX_DATA_BYTES = 8192;

    public static function normalizeType($type): ?string
    {
        $t = strtolower(trim((string) $type));
        return in_array($t, self::TYPES, true) ? $t : null;
    }

    /** REALTIME_BASE_URL (http/https) → URL do WebSocket do hub. */
    public static function wsUrl(string $base): ?string
    {
        $base = rtrim(trim($base), '/');
        if (stripos($base, 'https://') === 0) {
            return 'wss://' . substr($base, 8) . '/v1/ws';
        }
        if (stripos($base, 'http://') === 0) {
            return 'ws://' . substr($base, 7) . '/v1/ws';
        }
        return null;
    }

    public static function room(int $machinePk): string
    {
        return 'softprint:m:' . $machinePk;
    }

    public static function clipMessage($value): string
    {
        $s = preg_replace('/[\x00-\x1F\x7F]+/u', ' ', trim((string) $value)) ?? '';
        return mb_substr($s, 0, self::MAX_MESSAGE);
    }

    /** JSON do campo data, ou null se não for array ou passar de 8 KB. */
    public static function encodeData($data): ?string
    {
        if (!is_array($data)) {
            return null;
        }
        $json = json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        return ($json !== false && strlen($json) <= self::MAX_DATA_BYTES) ? $json : null;
    }
}
```

- [ ] **Step 5: Rodar e ver passar**

Run: `php scripts/test_softprint_commands.php`
Expected: todas as linhas `ok` e `Todos os testes passaram.`

- [ ] **Step 6: Commit**

```bash
git add src/Utils/SoftPrintCommandRules.php scripts/test_softprint_commands.php
git commit -m "feat(softprint): regras dos comandos remotos"
```

### Task A2: Schema — tabela de comandos e coluna `paused`

**Files:**
- Modify: `src/Utils/SoftPrintSchema.php`

- [ ] **Step 1: Subir a versão do marcador**

Troque `private const VERSION = 'v1';` por:

```php
    private const VERSION = 'v2';
```

- [ ] **Step 2: Acrescentar a tabela ao fim de `DDL` (antes do `];` que fecha o array)**

```php
        'SOFTPRINT_COMMANDS' => "CREATE TABLE IF NOT EXISTS SOFTPRINT_COMMANDS (
            id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
            emp_codigo     INT NOT NULL,
            machine_pk     BIGINT UNSIGNED NOT NULL,
            type           VARCHAR(32) NOT NULL,
            params         TEXT NULL,
            status         VARCHAR(16) NOT NULL DEFAULT 'pending',
            requested_by   VARCHAR(80) NOT NULL DEFAULT '',
            created_at     DATETIME NOT NULL,
            delivered_at   DATETIME NULL,
            finished_at    DATETIME NULL,
            result_message VARCHAR(500) NULL,
            result_data    TEXT NULL,
            KEY idx_softprint_cmd_machine (machine_pk, status),
            KEY idx_softprint_cmd_emp (emp_codigo, created_at)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",
```

- [ ] **Step 3: Acrescentar a lista de ALTERs logo depois do array `DDL`**

```php
    /** Colunas acrescentadas depois que a tabela já existia em produção. */
    public const ALTERS = [
        'SOFTPRINT_MACHINES.paused' => 'ALTER TABLE SOFTPRINT_MACHINES ADD COLUMN paused TINYINT(1) NOT NULL DEFAULT 0 AFTER processing',
    ];
```

- [ ] **Step 4: Rodar os ALTERs em `create()`**

Substitua o `return $results;` de `create()` por:

```php
        foreach (self::ALTERS as $name => $sql) {
            try {
                $pdo->exec($sql);
                $results[] = ['table' => $name, 'ok' => true];
            } catch (\PDOException $e) {
                // 1060 = coluna duplicada: já foi aplicado antes.
                $duplicate = (int) ($e->errorInfo[1] ?? 0) === 1060;
                $results[] = $duplicate
                    ? ['table' => $name, 'ok' => true]
                    : ['table' => $name, 'ok' => false, 'error' => $e->getMessage()];
            }
        }
        return $results;
```

- [ ] **Step 5: Também criar a coluna em instalações novas**

No DDL de `SOFTPRINT_MACHINES`, logo depois da linha `processing        INT NOT NULL DEFAULT 0,`, acrescente:

```php
            paused            TINYINT(1) NOT NULL DEFAULT 0,
```

(Em instalação nova o ALTER cai no erro 1060 e é tratado como ok.)

- [ ] **Step 6: Checar sintaxe**

Run: `php -l src/Utils/SoftPrintSchema.php`
Expected: `No syntax errors detected`

- [ ] **Step 7: Commit**

```bash
git add src/Utils/SoftPrintSchema.php
git commit -m "feat(softprint): tabela SOFTPRINT_COMMANDS e coluna paused"
```

### Task A3: Token do hub para a máquina e publicação em sala

**Files:**
- Modify: `src/Utils/RealtimeJwt.php`
- Modify: `src/Services/RealtimeHub.php`

- [ ] **Step 1: `RealtimeJwt::issueForSoftprintMachine`**

Acrescente em `RealtimeJwt`, antes de `private static function env`:

```php
    /** Validade do token do hub entregue ao SoftPrint. */
    public const SOFTPRINT_TTL = 43200; // 12 h

    /**
     * Token só para receber mensagens da sala da própria máquina.
     * @return array{token:string,expires_at:int}|null
     */
    public static function issueForSoftprintMachine(int $machinePk, string $room): ?array
    {
        $secret = self::secret();
        if ($secret === '') {
            return null;
        }
        $now = time();
        $exp = $now + self::SOFTPRINT_TTL;
        $payload = [
            'sub'           => 'softprint:' . $machinePk,
            'project'       => self::project(),
            'scopes'        => ['realtime.subscribe'],
            'allowed_rooms' => [$room],
            'iat'           => $now,
            'exp'           => $exp,
        ];
        $iss = trim((string) (self::env('REALTIME_JWT_ISSUER') ?? ''));
        if ($iss !== '') {
            $payload['iss'] = $iss;
        }
        $header = self::b64(json_encode(['alg' => 'HS256', 'typ' => 'JWT'], JSON_UNESCAPED_SLASHES));
        $body   = self::b64(json_encode($payload, JSON_UNESCAPED_SLASHES));
        $sig    = self::b64(hash_hmac('sha256', $header . '.' . $body, $secret, true));
        return ['token' => $header . '.' . $body . '.' . $sig, 'expires_at' => $exp];
    }

    /** URL base do hub (REALTIME_BASE_URL), vazia se não configurada. */
    public static function baseUrl(): string
    {
        return (string) (self::env('REALTIME_BASE_URL') ?? '');
    }
```

- [ ] **Step 2: `RealtimeHub::publishToRoom`**

Em `RealtimeHub`, troque o método `publish` privado por estas duas funções (a antiga passa a chamar a nova):

```php
    /** Publica em uma sala específica (ex.: softprint:m:42). Falha do hub só vai para o log. */
    public static function publishToRoom(string $room, string $type, array $payload): void
    {
        $pub = RealtimePublisher::fromEnv();
        if ($pub === null) {
            error_log('[RealtimeHub] skip publish: REALTIME_BASE_URL / REALTIME_API_KEY não configurados');
            return;
        }
        try {
            $result = $pub->publish(RealtimeJwt::project(), $room, $type, $payload);
            error_log('[RealtimeHub] published ' . $type . ' room=' . $room . ' id=' . ($result['id'] ?? '?'));
        } catch (\Throwable $e) {
            error_log('[RealtimeHub] publish failed: ' . $e->getMessage());
        }
    }

    private static function publish(string $type, array $payload): void
    {
        self::publishToRoom('monitor:all', $type, $payload);
    }
```

- [ ] **Step 3: Teste do token**

Acrescente ao fim de `scripts/test_softprint_commands.php`, **antes** do `echo $failures === 0 …`:

```php
// ── Token do hub para a máquina
$_ENV['REALTIME_JWT_SECRET'] = 'segredo-de-teste';
$_ENV['REALTIME_PROJECT'] = 'conex';
$issued = \App\Utils\RealtimeJwt::issueForSoftprintMachine(42, R::room(42));
[$h, $b, $s] = explode('.', $issued['token']);
$claims = json_decode(base64_decode(strtr($b, '-_', '+/')), true);
check('token: sala única', $claims['allowed_rooms'] === ['softprint:m:42']);
check('token: só subscribe', $claims['scopes'] === ['realtime.subscribe']);
check('token: sub da máquina', $claims['sub'] === 'softprint:42');
check('token: 12 h', $claims['exp'] - $claims['iat'] === 43200);
$expected = rtrim(strtr(base64_encode(hash_hmac('sha256', "$h.$b", 'segredo-de-teste', true)), '+/', '-_'), '=');
check('token: assinatura HS256', hash_equals($expected, $s));
unset($_ENV['REALTIME_JWT_SECRET']);
check('token: sem segredo vira null', \App\Utils\RealtimeJwt::issueForSoftprintMachine(1, 'x') === null);
```

- [ ] **Step 4: Rodar**

Run: `php scripts/test_softprint_commands.php`
Expected: todas `ok`.

- [ ] **Step 5: Commit**

```bash
git add src/Utils/RealtimeJwt.php src/Services/RealtimeHub.php scripts/test_softprint_commands.php
git commit -m "feat(softprint): token do hub por máquina e publicação em sala"
```

### Task A4: Serviço de comandos

**Files:**
- Create: `src/Services/SoftPrintCommandService.php`

- [ ] **Step 1: Implementar**

```php
<?php
namespace App\Services;

use App\Utils\SoftPrintCommandRules as Rules;
use App\Utils\SoftPrintSchema;
use PDO;

/**
 * Comandos do painel para uma máquina SoftPrint.
 * pending → delivered (SoftPrint buscou) → done | failed; pending velho → expired.
 */
class SoftPrintCommandService
{
    public const STATUS_PENDING   = 'pending';
    public const STATUS_DELIVERED = 'delivered';
    public const STATUS_DONE      = 'done';
    public const STATUS_FAILED    = 'failed';
    public const STATUS_EXPIRED   = 'expired';

    /** Sem resposta do SoftPrint depois de entregue (inclui o tempo de uma atualização). */
    private const DELIVERED_TIMEOUT_SECONDS = 900;
    private const HISTORY_DAYS = 90;

    private PDO $conn;

    public function __construct(PDO $conn)
    {
        $this->conn = $conn;
        SoftPrintSchema::ensure();
    }

    public static function pendingTtlSeconds(): int
    {
        $raw = $_ENV['SOFTPRINT_COMMAND_TTL_SECONDS'] ?? getenv('SOFTPRINT_COMMAND_TTL_SECONDS');
        $n   = is_numeric($raw) ? (int) $raw : 600;
        return max(60, min(86400, $n));
    }

    /** @return array{EMP_CODIGO:int,id:int}|null */
    public function machine(int $machinePk): ?array
    {
        $st = $this->conn->prepare('SELECT id, emp_codigo FROM SOFTPRINT_MACHINES WHERE id = ?');
        $st->execute([$machinePk]);
        $row = $st->fetch(PDO::FETCH_ASSOC);
        return $row ? ['id' => (int) $row['id'], 'EMP_CODIGO' => (int) $row['emp_codigo']] : null;
    }

    public function machinePkFor(int $empCodigo, string $machineId): ?int
    {
        $st = $this->conn->prepare('SELECT id FROM SOFTPRINT_MACHINES WHERE emp_codigo = ? AND machine_id = ?');
        $st->execute([$empCodigo, $machineId]);
        $id = $st->fetchColumn();
        return $id === false ? null : (int) $id;
    }

    /**
     * Cria o comando, ou devolve o mesmo tipo que ainda está aberto para a máquina.
     * @return array{command:array,created:bool}
     */
    public function create(int $machinePk, string $type, string $requestedBy): array
    {
        $norm = Rules::normalizeType($type);
        if ($norm === null) {
            throw new \InvalidArgumentException('Comando desconhecido.');
        }
        $machine = $this->machine($machinePk);
        if ($machine === null) {
            throw new \DomainException('Máquina não encontrada.');
        }

        $this->conn->beginTransaction();
        try {
            $open = $this->conn->prepare(
                'SELECT * FROM SOFTPRINT_COMMANDS
                 WHERE machine_pk = ? AND type = ? AND status IN (?, ?) ORDER BY id DESC LIMIT 1 FOR UPDATE'
            );
            $open->execute([$machinePk, $norm, self::STATUS_PENDING, self::STATUS_DELIVERED]);
            $existing = $open->fetch(PDO::FETCH_ASSOC);
            if ($existing) {
                $this->conn->commit();
                return ['command' => self::row($existing), 'created' => false];
            }

            $this->conn->prepare(
                'INSERT INTO SOFTPRINT_COMMANDS (emp_codigo, machine_pk, type, status, requested_by, created_at)
                 VALUES (?, ?, ?, ?, ?, NOW())'
            )->execute([$machine['EMP_CODIGO'], $machinePk, $norm, self::STATUS_PENDING, mb_substr($requestedBy, 0, 80)]);
            $id = (int) $this->conn->lastInsertId();
            $this->conn->commit();
        } catch (\Throwable $e) {
            if ($this->conn->inTransaction()) {
                $this->conn->rollBack();
            }
            throw $e;
        }

        RealtimeHub::publishToRoom(Rules::room($machinePk), 'softprint.command', ['id' => $id]);
        RealtimeHub::publishSoftprintUpdated(['emp_codigo' => $machine['EMP_CODIGO'], 'at' => date('c')]);
        return ['command' => $this->find($id), 'created' => true];
    }

    public function find(int $id): ?array
    {
        $st = $this->conn->prepare('SELECT * FROM SOFTPRINT_COMMANDS WHERE id = ?');
        $st->execute([$id]);
        $row = $st->fetch(PDO::FETCH_ASSOC);
        return $row ? self::row($row) : null;
    }

    public function listForMachine(int $machinePk, int $limit = 50): array
    {
        $limit = max(1, min(200, $limit));
        $st = $this->conn->prepare(
            'SELECT * FROM SOFTPRINT_COMMANDS WHERE machine_pk = ? ORDER BY id DESC LIMIT ' . $limit
        );
        $st->execute([$machinePk]);
        return array_map([self::class, 'row'], $st->fetchAll(PDO::FETCH_ASSOC));
    }

    /** Entrega (e marca delivered) os pendentes da máquina, na ordem de criação. */
    public function deliverPending(int $machinePk): array
    {
        $this->conn->beginTransaction();
        try {
            $st = $this->conn->prepare(
                'SELECT * FROM SOFTPRINT_COMMANDS WHERE machine_pk = ? AND status = ? ORDER BY id ASC FOR UPDATE'
            );
            $st->execute([$machinePk, self::STATUS_PENDING]);
            $rows = $st->fetchAll(PDO::FETCH_ASSOC);
            if ($rows) {
                $ids = array_map(fn ($r) => (int) $r['id'], $rows);
                $in  = implode(',', array_fill(0, count($ids), '?'));
                $this->conn->prepare(
                    "UPDATE SOFTPRINT_COMMANDS SET status = ?, delivered_at = NOW() WHERE id IN ($in)"
                )->execute(array_merge([self::STATUS_DELIVERED], $ids));
            }
            $this->conn->commit();
        } catch (\Throwable $e) {
            if ($this->conn->inTransaction()) {
                $this->conn->rollBack();
            }
            throw $e;
        }
        return array_map(fn ($r) => [
            'id'           => (int) $r['id'],
            'type'         => (string) $r['type'],
            'requested_by' => (string) $r['requested_by'],
        ], $rows);
    }

    /**
     * Grava o resultado. Comando de outra máquina → null (404). Já finalizado → devolve sem mudar.
     */
    public function saveResult(int $id, int $machinePk, bool $ok, $message, $data): ?array
    {
        $cmd = $this->find($id);
        if ($cmd === null || $cmd['machine_pk'] !== $machinePk) {
            return null;
        }
        if (in_array($cmd['status'], [self::STATUS_DONE, self::STATUS_FAILED, self::STATUS_EXPIRED], true)) {
            return $cmd;
        }
        $this->conn->prepare(
            'UPDATE SOFTPRINT_COMMANDS
             SET status = ?, finished_at = NOW(), result_message = ?, result_data = ?,
                 delivered_at = COALESCE(delivered_at, NOW())
             WHERE id = ?'
        )->execute([
            $ok ? self::STATUS_DONE : self::STATUS_FAILED,
            Rules::clipMessage($message),
            Rules::encodeData($data),
            $id,
        ]);
        RealtimeHub::publishSoftprintUpdated(['emp_codigo' => $cmd['emp_codigo'], 'at' => date('c')]);
        return $this->find($id);
    }

    public function pendingCount(int $machinePk): int
    {
        $st = $this->conn->prepare('SELECT COUNT(*) FROM SOFTPRINT_COMMANDS WHERE machine_pk = ? AND status = ?');
        $st->execute([$machinePk, self::STATUS_PENDING]);
        return (int) $st->fetchColumn();
    }

    /** Cron: expira pendentes velhos, falha entregues sem resposta e apaga histórico antigo. */
    public function runCron(): array
    {
        $expired = $this->conn->prepare(
            'UPDATE SOFTPRINT_COMMANDS SET status = ?, finished_at = NOW(),
                    result_message = \'Expirou: a máquina não buscou o comando a tempo.\'
             WHERE status = ? AND created_at < DATE_SUB(NOW(), INTERVAL ? SECOND)'
        );
        $expired->execute([self::STATUS_EXPIRED, self::STATUS_PENDING, self::pendingTtlSeconds()]);

        $stale = $this->conn->prepare(
            'UPDATE SOFTPRINT_COMMANDS SET status = ?, finished_at = NOW(),
                    result_message = \'Sem resposta do SoftPrint.\'
             WHERE status = ? AND delivered_at < DATE_SUB(NOW(), INTERVAL ? SECOND)'
        );
        $stale->execute([self::STATUS_FAILED, self::STATUS_DELIVERED, self::DELIVERED_TIMEOUT_SECONDS]);

        $old = $this->conn->prepare('DELETE FROM SOFTPRINT_COMMANDS WHERE created_at < DATE_SUB(NOW(), INTERVAL ? DAY)');
        $old->execute([self::HISTORY_DAYS]);

        $changed = $expired->rowCount() + $stale->rowCount();
        if ($changed > 0) {
            RealtimeHub::publishSoftprintUpdated(['at' => date('c')]);
        }
        return ['expired' => $expired->rowCount(), 'no_answer' => $stale->rowCount(), 'removed' => $old->rowCount()];
    }

    private static function row(array $r): array
    {
        return [
            'id'             => (int) $r['id'],
            'emp_codigo'     => (int) $r['emp_codigo'],
            'machine_pk'     => (int) $r['machine_pk'],
            'type'           => (string) $r['type'],
            'status'         => (string) $r['status'],
            'requested_by'   => (string) $r['requested_by'],
            'created_at'     => $r['created_at'],
            'delivered_at'   => $r['delivered_at'],
            'finished_at'    => $r['finished_at'],
            'result_message' => $r['result_message'],
            'result_data'    => $r['result_data'] !== null ? json_decode((string) $r['result_data'], true) : null,
        ];
    }
}
```

- [ ] **Step 2: Checar sintaxe**

Run: `php -l src/Services/SoftPrintCommandService.php`
Expected: `No syntax errors detected`

- [ ] **Step 3: Commit**

```bash
git add src/Services/SoftPrintCommandService.php
git commit -m "feat(softprint): serviço de comandos remotos"
```

### Task A5: Heartbeat com `paused` e lista de máquinas com comandos

**Files:**
- Modify: `src/Services/SoftPrintFleetService.php`

- [ ] **Step 1: Gravar `paused` no heartbeat**

Em `heartbeat()`, no array `$fields`, depois de `'processing' => …,` acrescente:

```php
            'paused'           => !empty($data['paused']) ? 1 : 0,
```

No `INSERT INTO SOFTPRINT_MACHINES`, troque a lista de colunas e valores por:

```php
                    'INSERT INTO SOFTPRINT_MACHINES
                        (emp_codigo, machine_id, hostname, app_version, platform, os, printing_backend,
                         pending, processing, paused, last_error, ip, status, offline_alerted,
                         first_seen_at, last_seen_at, status_changed_at)
                     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, NOW(), NOW(), NOW())'
                )->execute([
                    $empCodigo, $machineId, $fields['hostname'], $fields['app_version'], $fields['platform'],
                    $fields['os'], $fields['printing_backend'], $fields['pending'], $fields['processing'],
                    $fields['paused'], $fields['last_error'], $fields['ip'], $newStatus,
                ]);
```

No `UPDATE SOFTPRINT_MACHINES SET …` do heartbeat, troque `pending = ?, processing = ?, last_error = ?, ip = ?,` por `pending = ?, processing = ?, paused = ?, last_error = ?, ip = ?,` e, no `execute`, troque `$fields['processing'], $fields['last_error'],` por `$fields['processing'], $fields['paused'], $fields['last_error'],`.

- [ ] **Step 2: Lista de máquinas com `paused` e `pending_commands`**

Em `listMachines()`, troque o início do SELECT por:

```php
        $sql = 'SELECT m.id, m.emp_codigo, e.EMP_FANTASIA, m.machine_id, m.hostname, m.app_version, m.platform,
                       m.os, m.printing_backend, m.pending, m.processing, m.paused, m.last_error, m.ip, m.status,
                       m.first_seen_at, m.last_seen_at, m.status_changed_at,
                       TIMESTAMPDIFF(SECOND, m.last_seen_at, NOW()) AS seconds_since_seen,
                       (SELECT COUNT(*) FROM SOFTPRINT_COMMANDS c
                         WHERE c.machine_pk = m.id AND c.status IN (\'pending\', \'delivered\')) AS pending_commands
                FROM SOFTPRINT_MACHINES m
                LEFT JOIN EMPRESAS e ON e.EMP_CODIGO = m.emp_codigo';
```

e troque a lista de conversão por:

```php
            foreach (['id', 'emp_codigo', 'pending', 'processing', 'paused', 'seconds_since_seen', 'pending_commands'] as $k) {
```

- [ ] **Step 3: Apagar comandos junto com a máquina**

Em `deleteMachine()`, depois da linha que apaga de `SOFTPRINT_EVENTS`, acrescente:

```php
        $this->conn->prepare('DELETE FROM SOFTPRINT_COMMANDS WHERE machine_pk = ?')->execute([$id]);
```

- [ ] **Step 4: Cron dos comandos**

Em `runCron()`, troque o `return [` final por:

```php
        try {
            $commands = (new SoftPrintCommandService($this->conn))->runCron();
        } catch (\Throwable $e) {
            $commands = ['error' => $e->getMessage()];
        }

        return [
            'commands'         => $commands,
```

(mantendo as chaves que já existiam: `marked_offline`, `alerts`, `alerts_mode`, `packages_removed`).

- [ ] **Step 5: Checar sintaxe**

Run: `php -l src/Services/SoftPrintFleetService.php`
Expected: `No syntax errors detected`

- [ ] **Step 6: Commit**

```bash
git add src/Services/SoftPrintFleetService.php
git commit -m "feat(softprint): heartbeat com fila pausada e comandos na lista de máquinas"
```

### Task A6: Rotas do painel e do agente

**Files:**
- Modify: `src/Controllers/SoftPrintController.php`
- Modify: `src/Controllers/SoftPrintAgentController.php`
- Modify: `routes/softprint.php`

- [ ] **Step 1: Painel — criar comando e histórico**

Em `SoftPrintController`, acrescente `use App\Services\SoftPrintCommandService;` aos `use` e estes métodos antes de `settings()`:

```php
    /** POST /api/softprint/machines/{id}/commands {type} */
    public function createCommand(Request $request, Response $response, array $args): Response
    {
        $body  = (array) ($request->getParsedBody() ?? []);
        $actor = PanelActor::fromRequest($request, $body);
        $svc   = new SoftPrintCommandService(Connection::getInstance()->getConnection());
        try {
            $result = $svc->create((int) $args['id'], (string) ($body['type'] ?? ''), (string) $actor['usuario']);
        } catch (\InvalidArgumentException $e) {
            return ApiResponse::error($response, $e->getMessage(), 422);
        } catch (\DomainException $e) {
            return ApiResponse::error($response, $e->getMessage(), 404);
        }
        return ApiResponse::success($response, $result['command'], 1, $result['created'] ? 201 : 200);
    }

    /** GET /api/softprint/machines/{id}/commands?limit= */
    public function commands(Request $request, Response $response, array $args): Response
    {
        $limit = (int) ($request->getQueryParams()['limit'] ?? 50);
        $svc   = new SoftPrintCommandService(Connection::getInstance()->getConnection());
        return ApiResponse::success($response, $svc->listForMachine((int) $args['id'], $limit));
    }
```

- [ ] **Step 2: Agente — token do hub, comandos e resultado**

Em `SoftPrintAgentController`, acrescente aos `use`:

```php
use App\Database\Connection;
use App\Services\SoftPrintCommandService;
use App\Utils\RealtimeJwt;
use App\Utils\SoftPrintCommandRules;
```

Ainda em `heartbeat()`, troque o `return ApiResponse::success($response, [` por:

```php
        $pendingCommands = 0;
        try {
            $pendingCommands = (new SoftPrintCommandService(Connection::getInstance()->getConnection()))
                ->pendingCount((int) $result['machine_pk']);
        } catch (\Throwable $e) {
            error_log('SoftPrint heartbeat pending_commands: ' . $e->getMessage());
        }

        return ApiResponse::success($response, [
            'pending_commands'      => $pendingCommands,
```

E acrescente os métodos (antes de `clientIp`):

```php
    /** GET /api/softprint/agent/realtime-token?machineId= */
    public function realtimeToken(Request $request, Response $response): Response
    {
        $pk = $this->machinePk($request, (string) ($request->getQueryParams()['machineId'] ?? ''));
        if ($pk === null) {
            return ApiResponse::error($response, 'Máquina ainda não registrada: envie um heartbeat antes.', 404);
        }
        $ws = SoftPrintCommandRules::wsUrl(RealtimeJwt::baseUrl());
        $room = SoftPrintCommandRules::room($pk);
        $issued = $ws !== null ? RealtimeJwt::issueForSoftprintMachine($pk, $room) : null;
        if ($issued === null) {
            return ApiResponse::error($response, 'Tempo real não configurado no servidor.', 503);
        }
        return ApiResponse::success($response, [
            'ws_url'     => $ws,
            'token'      => $issued['token'],
            'room'       => $room,
            'expires_at' => gmdate('c', $issued['expires_at']),
        ]);
    }

    /** GET /api/softprint/agent/commands?machineId= — entrega os pendentes. */
    public function commands(Request $request, Response $response): Response
    {
        $pk = $this->machinePk($request, (string) ($request->getQueryParams()['machineId'] ?? ''));
        if ($pk === null) {
            return ApiResponse::success($response, []);
        }
        $svc = new SoftPrintCommandService(Connection::getInstance()->getConnection());
        return ApiResponse::success($response, $svc->deliverPending($pk));
    }

    /** POST /api/softprint/agent/commands/{id}/result {machineId, ok, message, data} */
    public function commandResult(Request $request, Response $response, array $args): Response
    {
        $body = $request->getParsedBody();
        if (!is_array($body)) {
            return ApiResponse::error($response, 'Corpo JSON inválido.', 400);
        }
        $pk = $this->machinePk($request, (string) ($body['machineId'] ?? ''));
        $svc = new SoftPrintCommandService(Connection::getInstance()->getConnection());
        $saved = $pk === null ? null : $svc->saveResult(
            (int) $args['id'], $pk, !empty($body['ok']), $body['message'] ?? '', $body['data'] ?? null
        );
        if ($saved === null) {
            return ApiResponse::error($response, 'Comando não encontrado.', 404);
        }
        return ApiResponse::success($response, $saved);
    }

    private function machinePk(Request $request, string $machineId): ?int
    {
        $empresa = $request->getAttribute('softprint_empresa');
        $machineId = trim($machineId);
        if ($machineId === '' || !preg_match('/^[A-Za-z0-9._:-]{1,64}$/', $machineId)) {
            return null;
        }
        $svc = new SoftPrintCommandService(Connection::getInstance()->getConnection());
        return $svc->machinePkFor((int) $empresa['EMP_CODIGO'], $machineId);
    }
```

- [ ] **Step 3: Rotas**

Em `routes/softprint.php`, no grupo `/api/softprint/agent`, depois da linha do `support-packages`:

```php
        $group->get('/realtime-token', [SoftPrintAgentController::class, 'realtimeToken']);
        $group->get('/commands', [SoftPrintAgentController::class, 'commands']);
        $group->post('/commands/{id:[0-9]+}/result', [SoftPrintAgentController::class, 'commandResult']);
```

No grupo do painel, depois da linha do `DELETE /machines/{id}`:

```php
        $group->get('/machines/{id:[0-9]+}/commands', [SoftPrintController::class, 'commands']);
        $group->post('/machines/{id:[0-9]+}/commands', [SoftPrintController::class, 'createCommand']);
```

- [ ] **Step 4: Checar sintaxe e rotas**

Run: `php -l src/Controllers/SoftPrintController.php && php -l src/Controllers/SoftPrintAgentController.php && php scripts/check_routes.php | tail -1 && php scripts/test_auth.php | tail -1`
Expected: sem erros de sintaxe, `… rotas verificadas, 0 erro(s).` e `Todos os testes passaram.`

- [ ] **Step 5: Commit**

```bash
git add src/Controllers/SoftPrintController.php src/Controllers/SoftPrintAgentController.php routes/softprint.php
git commit -m "feat(softprint): rotas de comandos (painel e agente) e token do hub"
```

### Task A7: CI e documentação

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `docs/softprint-frota.md`
- Modify: `.env.example`

- [ ] **Step 1: CI**

Em `.github/workflows/ci.yml`, depois do passo `Regras de autenticação`, acrescente:

```yaml
      - name: Comandos do SoftPrint
        run: php scripts/test_softprint_commands.php
```

- [ ] **Step 2: `.env.example`**

Depois de `SOFTPRINT_PACKAGE_RETENTION_DAYS=30`:

```
# Segundos que um comando do painel espera a máquina buscar antes de expirar
SOFTPRINT_COMMAND_TTL_SECONDS=600
```

- [ ] **Step 3: Documentação**

Acrescente ao fim de `docs/softprint-frota.md`:

```markdown
## Comandos remotos e tempo real

O painel cria comandos para uma máquina; o SoftPrint recebe o aviso pelo hub (sala `softprint:m:<id>`)
ou pelo `pending_commands` do heartbeat, busca os comandos e devolve o resultado.

Tipos: `update`, `pause`, `resume`, `reprint_failed`, `test_print`, `support_package`, `refresh`.

Situação: `pending` → `delivered` → `done` | `failed`; `pending` há mais de `SOFTPRINT_COMMAND_TTL_SECONDS`
(600) vira `expired`; `delivered` há mais de 15 min vira `failed` ("Sem resposta do SoftPrint").
Histórico guardado por 90 dias.

Painel (perfil administrativo):

- `POST /api/softprint/machines/{id}/commands` `{"type":"pause"}` — 201 (ou 200 com o comando já aberto do mesmo tipo)
- `GET /api/softprint/machines/{id}/commands?limit=`

SoftPrint (token da empresa):

- `GET /api/softprint/agent/realtime-token?machineId=` — `{ws_url, token, room, expires_at}`; 503 sem `REALTIME_JWT_SECRET`
- `GET /api/softprint/agent/commands?machineId=` — entrega os pendentes (marca `delivered`)
- `POST /api/softprint/agent/commands/{id}/result` `{machineId, ok, message, data}`

O token do hub vale 12 h, só para receber (`realtime.subscribe`) e só na sala da máquina (`allowed_rooms`).
Os dois servidores (VPS e hospedagem) precisam de `REALTIME_BASE_URL`, `REALTIME_API_KEY` e `REALTIME_JWT_SECRET`.
```

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml docs/softprint-frota.md .env.example
git commit -m "docs(softprint): comandos remotos e tempo real"
```

---

## Parte B — SoftPrint

Trabalhe em `C:\Users\pc\Desktop\mednews\mednews_web\SoftPrint`, branch `feat/comandos-remotos`.
Testes: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0` (o net6 não roda nesta máquina; a CI roda).

### Task B1: Impressão de teste reutilizável

**Files:**
- Create: `SoftPrint.Application/Services/GuidedTestPrint.cs`
- Modify: `SoftPrint.Api/Endpoints/AppEndpoints.cs` (bloco `MapPost("/api/jobs/guided-test"`)
- Modify: `SoftPrint.Host.Windows.Legacy/Controllers/AppController.cs` (`GuidedTest()`)

- [ ] **Step 1: Criar**

```csharp
using SoftPrint.Domain;

namespace SoftPrint.Application.Services;

/// <summary>Etiqueta de teste guiado: mesma para a tela, o Legacy e o comando remoto.</summary>
public static class GuidedTestPrint
{
    public static PrintJob Submit(JobQueueService jobs, SettingsService settings, string? origin = null)
    {
        var paper = settings.Current;
        var text =
            "TESTE GUIADO SOFTPRINT\n" +
            "----------------------\n" +
            $"Papel: {PrintSurfaceMapper.Describe(paper)}\n" +
            $"Impressora: {paper.PrinterName}\n" +
            $"Modo: {(paper.Simulation ? "simulação" : "real")}\n" +
            $"Quando: {DateTimeOffset.Now:HH:mm:ss}\n" +
            (string.IsNullOrWhiteSpace(origin) ? "" : $"Pedido por: {origin}\n") +
            "\n" +
            "Se o papel estiver em Padrão (200×70),\n" +
            "esta etiqueta deve caber na área imprimível.";
        return jobs.Submit(
            reference: "guia-" + DateTimeOffset.Now.ToString("HHmmss"),
            text: text,
            jobType: "default",
            templateName: null);
    }
}
```

- [ ] **Step 2: Usar na API**

Em `AppEndpoints.cs`, dentro do `try` de `MapPost("/api/jobs/guided-test"`, troque tudo do `var paper = settings.Current;` até o `templateName: null);` por:

```csharp
                var result = GuidedTestPrint.Submit(jobs, settings);
```

- [ ] **Step 3: Usar no Legacy**

Em `AppController.GuidedTest()`, troque o mesmo trecho por:

```csharp
                var result = GuidedTestPrint.Submit(_jobs, _settings);
```

- [ ] **Step 4: Compilar e rodar os testes**

Run: `dotnet build SoftPrint.Host.Windows.Legacy/SoftPrint.Host.Windows.Legacy.csproj -c Release 2>&1 | grep -E "Erro\(s\)|error"` e `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 2>&1 | tail -1`
Expected: `0 Erro(s)` e `Aprovado!` com 87 testes.

- [ ] **Step 5: Commit**

```bash
git add SoftPrint.Application/Services/GuidedTestPrint.cs SoftPrint.Api/Endpoints/AppEndpoints.cs SoftPrint.Host.Windows.Legacy/Controllers/AppController.cs
git commit -m "refactor: impressão de teste guiada reutilizável"
```

### Task B2: Contratos novos do monitoramento

**Files:**
- Modify: `SoftPrint.Application/Abstractions/IFleetService.cs`

- [ ] **Step 1: Tipos e métodos**

Troque o arquivo inteiro por:

```csharp
namespace SoftPrint.Application.Abstractions;

/// <summary>Situação da ligação com o monitoramento Softnew (backendempresa).</summary>
public sealed record FleetStatus(
    bool Configured,
    bool TokenFromEnv,
    string TokenHint,
    string MachineId,
    string Hostname,
    string Url,
    int HeartbeatSeconds,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    bool? LastOk,
    string? LastMessage,
    string? Empresa,
    bool RealtimeConnected = false,
    string? LastCommand = null);

public sealed record FleetSendResult(bool Ok, string Message, int? StatusCode = null, int PendingCommands = 0);

/// <summary>Comando do painel para esta máquina.</summary>
public sealed record FleetCommand(long Id, string Type, string? RequestedBy);

/// <summary>Credencial para o WebSocket do hub (só a sala desta máquina).</summary>
public sealed record FleetRealtimeTicket(Uri WsUrl, string Token, string Room, DateTimeOffset ExpiresAt);

/// <summary>Atualização pedida pelo painel, cujo resultado só sai depois do reinício.</summary>
public sealed record FleetPendingUpdate(long CommandId, string FromVersion);

/// <summary>
/// Heartbeat, comandos e pacote de suporte com o backend da Softnew, usando o token da empresa.
/// Sem token configurado, nada é enviado.
/// </summary>
public interface IFleetService
{
    FleetStatus GetStatus();

    /// <summary>Grava o token (vazio apaga o token salvo; o do .env continua valendo).</summary>
    void SetToken(string? token);

    /// <summary>
    /// Troca o token como <see cref="SetToken"/>, mas antes avisa "stopping" com o token antigo:
    /// sem isso o cadastro antigo da máquina para de receber sinal e o painel acusa queda.
    /// </summary>
    Task ChangeTokenAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>Cancelado sempre que o token muda (o WebSocket reconecta com o novo).</summary>
    CancellationToken TokenChanged { get; }

    /// <param name="kind">"heartbeat" ou "stopping" (SoftPrint fechando normalmente).</param>
    Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default);

    Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default);

    /// <summary>Busca (e o backend marca como entregues) os comandos pendentes.</summary>
    Task<IReadOnlyList<FleetCommand>> GetCommandsAsync(CancellationToken cancellationToken = default);

    Task<bool> SendCommandResultAsync(long commandId, bool ok, string message, CancellationToken cancellationToken = default);

    /// <summary>Null quando o servidor não tem tempo real ou a máquina ainda não foi registrada.</summary>
    Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default);

    void SetRealtimeConnected(bool connected);

    void NoteCommand(string description);

    void RememberPendingUpdate(long commandId);

    FleetPendingUpdate? GetPendingUpdate();

    void ClearPendingUpdate();
}
```

- [ ] **Step 2: Compilar (vai falhar no FleetService — esperado)**

Run: `dotnet build SoftPrint.Infrastructure.Core -f net10.0 2>&1 | grep -c "error CS0535"`
Expected: um número maior que 0 (`FleetService` ainda não implementa os membros novos).

(Sem commit aqui: o build só volta a passar no fim da Task B3.)

### Task B3: FleetService — comandos, token do hub, `paused` e update pendente

**Files:**
- Modify: `SoftPrint.Infrastructure.Core/Integrations/FleetService.cs`
- Modify: `SoftPrint.Tests/FleetServiceTests.cs`

- [ ] **Step 1: Testes que falham**

Em `FleetServiceTests.cs`, troque o `RecordingHandler` por uma versão que aceita uma resposta por URL e acrescente os testes. Substitua a classe `RecordingHandler` inteira por:

```csharp
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];
        public Dictionary<string, (HttpStatusCode Status, string Body)> ByPath { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-SoftPrint-Token", out var t) ? t.FirstOrDefault() : null,
                request.Content?.Headers.ContentType?.ToString() ?? "",
                content));
            var (s, b) = ByPath.TryGetValue(request.RequestUri!.AbsolutePath, out var r) ? r : (status, body);
            return new HttpResponseMessage(s) { Content = new StringContent(b) };
        }
    }
```

E acrescente, antes de `private FleetService Create(`:

```csharp
    [Fact]
    public async Task Heartbeat_ReadsPendingCommands_AndSendsPaused()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"status":"success","data":{"ok":true,"pending_commands":2}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        var result = await fleet.SendHeartbeatAsync();

        Assert.Equal(2, result.PendingCommands);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(body.RootElement.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task GetCommands_ParsesList_WithMachineId()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        handler.ByPath["/api/softprint/agent/commands"] = (HttpStatusCode.OK,
            """{"status":"success","data":[{"id":7,"type":"pause","requested_by":"ana"},{"id":8,"type":"refresh","requested_by":""}]}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        var cmds = await fleet.GetCommandsAsync();

        Assert.Equal(new[] { 7L, 8L }, cmds.Select(c => c.Id));
        Assert.Equal("pause", cmds[0].Type);
        Assert.Equal("ana", cmds[0].RequestedBy);
        Assert.Contains("machineId=" + fleet.GetStatus().MachineId, handler.Requests[0].Url);
    }

    [Fact]
    public async Task SendCommandResult_PostsOkAndMessage()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"status":"success","data":{}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        Assert.True(await fleet.SendCommandResultAsync(7, true, "Fila pausada"));

        var req = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/softprint/agent/commands/7/result", req.Url);
        using var body = JsonDocument.Parse(req.Body);
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Fila pausada", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(fleet.GetStatus().MachineId, body.RootElement.GetProperty("machineId").GetString());
    }

    [Fact]
    public async Task RealtimeTicket_ParsesUrlTokenAndExpiry_OrNullWhenUnavailable()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        handler.ByPath["/api/softprint/agent/realtime-token"] = (HttpStatusCode.OK,
            """{"status":"success","data":{"ws_url":"wss://hub.test/v1/ws","token":"jwt","room":"softprint:m:3","expires_at":"2026-11-15T10:00:00+00:00"}}""");
        var fleet = Create(handler);
        fleet.SetToken("sp_1_x000000000000");

        var ticket = await fleet.GetRealtimeTicketAsync();

        Assert.NotNull(ticket);
        Assert.Equal(new Uri("wss://hub.test/v1/ws"), ticket!.WsUrl);
        Assert.Equal("softprint:m:3", ticket.Room);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero), ticket.ExpiresAt);

        handler.ByPath["/api/softprint/agent/realtime-token"] = (HttpStatusCode.ServiceUnavailable, """{"message":"off"}""");
        Assert.Null(await fleet.GetRealtimeTicketAsync());
    }

    [Fact]
    public void PendingUpdate_SurvivesRestart_UntilCleared()
    {
        var fleet = Create(new RecordingHandler(HttpStatusCode.OK, "{}"));
        fleet.RememberPendingUpdate(42);

        var again = Create(new RecordingHandler(HttpStatusCode.OK, "{}"));
        var pending = again.GetPendingUpdate();
        Assert.Equal(42, pending!.CommandId);
        Assert.Equal(SoftPrintVersion.Current, pending.FromVersion);

        again.ClearPendingUpdate();
        Assert.Null(Create(new RecordingHandler(HttpStatusCode.OK, "{}")).GetPendingUpdate());
    }

    [Fact]
    public void TokenChanged_IsCancelled_WhenTokenIsSet()
    {
        var fleet = Create(new RecordingHandler(HttpStatusCode.OK, "{}"));
        var before = fleet.TokenChanged;

        fleet.SetToken("sp_1_x000000000000");

        Assert.True(before.IsCancellationRequested);
        Assert.False(fleet.TokenChanged.IsCancellationRequested);
    }
```

E acrescente `using SoftPrint.Domain;` no topo, se ainda não houver.

- [ ] **Step 2: Implementar no `FleetService`**

2a. Campos novos, logo depois de `private string? _empresa;`:

```csharp
    private readonly ISettingsRepository? _settings;
    private CancellationTokenSource _tokenChanged = new();
    private bool _realtimeConnected;
    private string? _lastCommand;
```

2b. Construtor: acrescente o parâmetro opcional no fim e guarde-o:

```csharp
        ILogger<FleetService> logger,
        ISettingsRepository? settings = null)
    {
        _settings = settings;
```

(mantendo as atribuições que já existem).

2c. Em `GetStatus()`, troque `Empresa: _empresa);` por:

```csharp
                Empresa: _empresa,
                RealtimeConnected: _realtimeConnected,
                LastCommand: _lastCommand);
```

2d. Em `SetToken`, depois de `_empresa = null;`:

```csharp
            var previous = _tokenChanged;
            _tokenChanged = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
```

2e. Membros novos (coloque depois de `SetToken`):

```csharp
    public CancellationToken TokenChanged
    {
        get { lock (_gate) return _tokenChanged.Token; }
    }

    public void SetRealtimeConnected(bool connected)
    {
        lock (_gate) _realtimeConnected = connected;
    }

    public void NoteCommand(string description)
    {
        lock (_gate) _lastCommand = $"{DateTimeOffset.Now:HH:mm:ss} {description}";
    }

    public void RememberPendingUpdate(long commandId)
    {
        lock (_gate)
        {
            _file = _file with { PendingUpdateCommandId = commandId, PendingUpdateFromVersion = SoftPrintVersion.Current };
            Save();
        }
    }

    public FleetPendingUpdate? GetPendingUpdate()
    {
        lock (_gate)
            return _file.PendingUpdateCommandId is long id
                ? new FleetPendingUpdate(id, _file.PendingUpdateFromVersion ?? "")
                : null;
    }

    public void ClearPendingUpdate()
    {
        lock (_gate)
        {
            _file = _file with { PendingUpdateCommandId = null, PendingUpdateFromVersion = null };
            Save();
        }
    }

    public async Task<IReadOnlyList<FleetCommand>> GetCommandsAsync(CancellationToken cancellationToken = default)
    {
        var token = EffectiveToken;
        if (token.Length == 0) return [];
        using var client = _httpClientFactory.CreateClient("softprint-fleet");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            BaseUrl() + "/api/softprint/agent/commands?machineId=" + Uri.EscapeDataString(MachineId()));
        AddHeaders(request, token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var list = new List<FleetCommand>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var idValue)) continue;
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var by = item.TryGetProperty("requested_by", out var r) ? r.GetString() : null;
                    list.Add(new FleetCommand(idValue, type, string.IsNullOrWhiteSpace(by) ? null : by));
                }
            }
        }
        catch (JsonException) { /* resposta inválida = sem comandos */ }
        return list;
    }

    public async Task<bool> SendCommandResultAsync(long commandId, bool ok, string message, CancellationToken cancellationToken = default)
    {
        var token = EffectiveToken;
        if (token.Length == 0) return false;
        try
        {
            using var client = _httpClientFactory.CreateClient("softprint-fleet");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                BaseUrl() + $"/api/softprint/agent/commands/{commandId}/result")
            {
                Content = JsonContent.Create(new { machineId = MachineId(), ok, message }, options: JsonOptions)
            };
            AddHeaders(request, token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Falha ao enviar resultado do comando {Id}", commandId);
            return false;
        }
    }

    public async Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default)
    {
        var token = EffectiveToken;
        if (token.Length == 0) return null;
        try
        {
            using var client = _httpClientFactory.CreateClient("softprint-fleet");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                BaseUrl() + "/api/softprint/agent/realtime-token?machineId=" + Uri.EscapeDataString(MachineId()));
            AddHeaders(request, token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            var url = data.GetProperty("ws_url").GetString();
            var jwt = data.GetProperty("token").GetString();
            var room = data.GetProperty("room").GetString();
            var expires = data.GetProperty("expires_at").GetDateTimeOffset();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(jwt) || string.IsNullOrWhiteSpace(room))
                return null;
            return new FleetRealtimeTicket(new Uri(url!), jwt!, room!, expires);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Token do tempo real indisponível");
            return null;
        }
    }
```

2f. `paused` no heartbeat: no objeto `payload` de `SendHeartbeatCoreAsync`, depois de `processing,`:

```csharp
            paused = _settings?.Current.Paused ?? false,
```

2g. `pending_commands` na resposta: em `Finish`, logo depois do bloco que lê `empresa` (dentro do `try`, ainda com `data` disponível), troque o bloco `try { … } catch (JsonException) { … }` por:

```csharp
            var pendingCommands = 0;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("empresa", out var emp) && emp.ValueKind == JsonValueKind.String)
                        empresa = emp.GetString();
                    if (data.TryGetProperty("pending_commands", out var pc) && pc.TryGetInt32(out var n))
                        pendingCommands = n;
                }
            }
            catch (JsonException) { /* resposta sem JSON: segue como sucesso */ }
```

e troque o `return new FleetSendResult(true, …, (int)status);` desse ramo por:

```csharp
            return new FleetSendResult(true, empresa is null ? okMessage : $"{okMessage} Empresa: {empresa}.", (int)status, pendingCommands);
```

2h. `FleetFile` ganha os campos do update pendente:

```csharp
    private sealed record FleetFile
    {
        public string? Token { get; init; }
        public string? MachineId { get; init; }
        public long? PendingUpdateCommandId { get; init; }
        public string? PendingUpdateFromVersion { get; init; }
    }
```

- [ ] **Step 3: Rodar os testes**

Run: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 --filter "FullyQualifiedName~FleetServiceTests" 2>&1 | tail -1`
Expected: `Aprovado!` com 20 testes.

- [ ] **Step 4: Commit**

```bash
git add SoftPrint.Application/Abstractions/IFleetService.cs SoftPrint.Infrastructure.Core/Integrations/FleetService.cs SoftPrint.Tests/FleetServiceTests.cs
git commit -m "feat: monitoramento busca comandos, devolve resultado e pede token do tempo real"
```

### Task B4: `UpdateFailureNotice.Peek`

**Files:**
- Modify: `SoftPrint.Infrastructure.Core/Updates/LocalPreviousVersionStore.cs`

- [ ] **Step 1: Acrescentar depois de `TryConsume()`**

```csharp
    /// <summary>Lê os avisos pendentes sem apagar (a tela ainda vai mostrá-los ao usuário).</summary>
    public static string? Peek()
    {
        var chunks = new List<string>();
        foreach (var path in new[] { PersistentErrorPath, ScriptErrorPath, TempErrorPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var body = File.ReadAllText(path).Trim();
                if (body.Length > 0) chunks.Add(body);
            }
            catch { /* ignore */ }
        }
        return chunks.Count == 0 ? null : string.Join(" ", chunks.Distinct());
    }
```

- [ ] **Step 2: Compilar**

Run: `dotnet build SoftPrint.Infrastructure.Core -f net10.0 2>&1 | grep -E "Erro\(s\)"`
Expected: `0 Erro(s)`

- [ ] **Step 3: Commit**

```bash
git add SoftPrint.Infrastructure.Core/Updates/LocalPreviousVersionStore.cs
git commit -m "feat: ler aviso de falha de atualização sem consumir"
```

### Task B5: Executor de comandos

**Files:**
- Create: `SoftPrint.Infrastructure.Core/Integrations/FleetCommandRunner.cs`
- Create: `SoftPrint.Tests/FleetCommandRunnerTests.cs`

- [ ] **Step 1: Testes que falham**

`SoftPrint.Tests/FleetCommandRunnerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.Infrastructure.Persistence;
using Xunit;

namespace SoftPrint.Tests;

public sealed class FleetCommandRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softprint-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFleet _fleet = new();
    private readonly FakeUpdates _updates = new();
    private readonly JsonSettingsRepository _settingsRepo;
    private readonly SettingsService _settings;
    private readonly JsonJobRepository _jobsRepo;
    private readonly JobQueueService _jobs;
    private readonly FleetCommandRunner _runner;

    public FleetCommandRunnerTests()
    {
        Directory.CreateDirectory(_root);
        var paths = new TestPaths(_root);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        _settingsRepo = new JsonSettingsRepository(paths, config);
        _settings = new SettingsService(_settingsRepo, new NoPrinters());
        _jobsRepo = new JsonJobRepository(paths);
        _jobs = new JobQueueService(_jobsRepo, new PrintJobFactory(),
            new TemplateRenderer(Options.Create(new SoftPrintFeatureOptions())));
        _runner = new FleetCommandRunner(_fleet, _updates, _settings, _jobs, new FakeSupport(_root),
            NullLogger<FleetCommandRunner>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task Pause_ThenResume_ChangesSettings_AndReportsDone()
    {
        _fleet.Queue(new FleetCommand(1, "pause", "ana"), new FleetCommand(2, "resume", "ana"));

        await _runner.PollAsync();

        Assert.False(_settings.Current.Paused);
        Assert.Equal(new[] { (1L, true, "Fila pausada."), (2L, true, "Fila retomada.") }, _fleet.Results);
    }

    [Fact]
    public async Task UnknownType_ReportsFailure()
    {
        _fleet.Queue(new FleetCommand(3, "format_c", null));

        await _runner.PollAsync();

        Assert.Equal(new[] { (3L, false, "Comando desconhecido: format_c") }, _fleet.Results);
    }

    [Fact]
    public async Task SameIdTwice_RunsOnce()
    {
        _fleet.Queue(new FleetCommand(4, "refresh", null));
        await _runner.PollAsync();
        _fleet.Queue(new FleetCommand(4, "refresh", null));
        await _runner.PollAsync();

        Assert.Single(_fleet.Results);
        Assert.Equal(1, _fleet.Heartbeats);
    }

    [Fact]
    public async Task TestPrint_SubmitsGuidedJob()
    {
        _fleet.Queue(new FleetCommand(5, "test_print", "ana"));

        await _runner.PollAsync();

        var job = Assert.Single(_jobs.List());
        Assert.Contains("Pedido por: ana", job.Text);
        Assert.True(_fleet.Results[0].Ok);
    }

    [Fact]
    public async Task ReprintFailed_ReprintsOnlyRecentUncertain()
    {
        var failed = _jobs.Submit("ref-falhou", "texto");
        _jobsRepo.TakePending(failed.Id, "imp", 1);
        _jobsRepo.Finish(failed.Id, JobStatus.Uncertain, "papel acabou");
        _fleet.Queue(new FleetCommand(6, "reprint_failed", null));

        await _runner.PollAsync();

        Assert.Equal((6L, true, "1 trabalho(s) reenviado(s)."), _fleet.Results[0]);
        Assert.Equal(2, _jobs.List().Count);
    }

    [Fact]
    public async Task Update_StartsApplier_AndWaitsForRestart()
    {
        _fleet.Queue(new FleetCommand(7, "update", null));

        await _runner.PollAsync();

        Assert.True(_updates.Started);
        Assert.Empty(_fleet.Results);
        Assert.Equal(7, _fleet.PendingUpdate!.CommandId);
    }

    [Fact]
    public async Task Update_WhenApplierRefuses_ReportsFailure()
    {
        _updates.Refuse = "Já está na versão mais nova.";
        _fleet.Queue(new FleetCommand(8, "update", null));

        await _runner.PollAsync();

        Assert.Equal(new[] { (8L, false, "Já está na versão mais nova.") }, _fleet.Results);
        Assert.Null(_fleet.PendingUpdate);
    }

    [Fact]
    public async Task PendingUpdate_AfterRestart_ReportsNewVersion()
    {
        _fleet.PendingUpdate = new FleetPendingUpdate(9, "0.0.1");

        await _runner.ReportPendingUpdateAsync();

        Assert.Equal((9L, true, $"Atualizado de 0.0.1 para {SoftPrintVersion.Current}."), _fleet.Results[0]);
        Assert.Null(_fleet.PendingUpdate);
    }

    [Fact]
    public async Task PendingUpdate_SameVersion_ReportsFailure()
    {
        _fleet.PendingUpdate = new FleetPendingUpdate(10, SoftPrintVersion.Current);

        await _runner.ReportPendingUpdateAsync();

        Assert.False(_fleet.Results[0].Ok);
        Assert.StartsWith("A atualização não foi concluída", _fleet.Results[0].Message);
    }

    [Fact]
    public async Task SupportPackage_IsUploaded()
    {
        _fleet.Queue(new FleetCommand(11, "support_package", "ana"));

        await _runner.PollAsync();

        Assert.Equal("Pedido pelo painel por ana", _fleet.UploadedNote);
        Assert.True(_fleet.Results[0].Ok);
    }

    private sealed record TestPaths(string Root) : IAppPaths
    {
        public string DataRoot => Path.Combine(Root, "data");
        public string ConfigRoot => Path.Combine(Root, "config");
    }

    private sealed class NoPrinters : IPrinterCatalog
    {
        public IReadOnlyList<string> ListInstalled() => [];
        public IReadOnlyList<PrinterDeviceInfo> ListDetailed() => [];
        public bool IsInstalled(string printerName) => true;
    }

    private sealed class FakeSupport(string root) : ISupportBundleService
    {
        public string CreateBundle()
        {
            var path = Path.Combine(root, "pacote.zip");
            File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04]);
            return path;
        }
    }

    private sealed class FakeUpdates : IUpdateApplier
    {
        public bool Started { get; private set; }
        public string? Refuse { get; set; }
        public UpdateApplyStatus Status => GetStatus();

        public bool TryStart(out string? error)
        {
            error = Refuse;
            Started = Refuse is null;
            return Started;
        }

        public bool TryStartRollback(string? targetVersion, out string? error) => throw new NotSupportedException();
        public UpdateApplyStatus GetStatus() => new("idle", 0, "", false, false, false);
    }

    internal sealed class FakeFleet : IFleetService
    {
        private readonly Queue<FleetCommand> _queue = new();
        public List<(long Id, bool Ok, string Message)> Results { get; } = [];
        public FleetPendingUpdate? PendingUpdate { get; set; }
        public int Heartbeats { get; private set; }
        public string? UploadedNote { get; private set; }

        public void Queue(params FleetCommand[] commands)
        {
            foreach (var c in commands) _queue.Enqueue(c);
        }

        public Task<IReadOnlyList<FleetCommand>> GetCommandsAsync(CancellationToken cancellationToken = default)
        {
            var list = _queue.ToList();
            _queue.Clear();
            return Task.FromResult<IReadOnlyList<FleetCommand>>(list);
        }

        public Task<bool> SendCommandResultAsync(long commandId, bool ok, string message, CancellationToken cancellationToken = default)
        {
            Results.Add((commandId, ok, message));
            return Task.FromResult(true);
        }

        public Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default)
        {
            Heartbeats++;
            return Task.FromResult(new FleetSendResult(true, "ok"));
        }

        public Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default)
        {
            UploadedNote = note;
            return Task.FromResult(new FleetSendResult(true, "Pacote enviado ao suporte Softnew."));
        }

        public void RememberPendingUpdate(long commandId) => PendingUpdate = new FleetPendingUpdate(commandId, SoftPrintVersion.Current);
        public FleetPendingUpdate? GetPendingUpdate() => PendingUpdate;
        public void ClearPendingUpdate() => PendingUpdate = null;

        public FleetStatus GetStatus() => new(true, false, "", "m1", "pc", "https://b.test", 40, null, null, true, null, null);
        public void SetToken(string? token) { }
        public Task ChangeTokenAsync(string? token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public CancellationToken TokenChanged => CancellationToken.None;
        public Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default) => Task.FromResult<FleetRealtimeTicket?>(null);
        public void SetRealtimeConnected(bool connected) { }
        public void NoteCommand(string description) { }
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 --filter "FullyQualifiedName~FleetCommandRunnerTests" 2>&1 | grep -E "error CS|Aprovado|Falha" | head -3`
Expected: `error CS0246: … 'FleetCommandRunner' could not be found`

- [ ] **Step 3: Implementar**

`SoftPrint.Infrastructure.Core/Integrations/FleetCommandRunner.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Updates;

namespace SoftPrint.Infrastructure.Integrations;

/// <summary>Quem dispara a busca de comandos (WebSocket e heartbeat).</summary>
public interface IFleetCommandPoller
{
    Task<int> PollAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Executa os comandos do painel: um por vez, cada id uma vez só. Lista fechada de tipos.
/// O resultado do "update" sai depois do reinício (<see cref="ReportPendingUpdateAsync"/>).
/// </summary>
public sealed class FleetCommandRunner(
    IFleetService fleet,
    IUpdateApplier updates,
    SettingsService settings,
    JobQueueService jobs,
    ISupportBundleService support,
    ILogger<FleetCommandRunner> log) : IFleetCommandPoller
{
    private const int RememberIds = 200;
    private const int MaxReprint = 50;
    private const int MaxMessage = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<long> _doneOrder = new();
    private readonly HashSet<long> _done = new();

    private sealed record Outcome(bool Ok, string Message);

    public async Task<int> PollAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var commands = await fleet.GetCommandsAsync(cancellationToken).ConfigureAwait(false);
            var ran = 0;
            foreach (var command in commands)
            {
                if (!Remember(command.Id)) continue;
                fleet.NoteCommand(command.Type);
                var outcome = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (outcome is not null)
                    await fleet.SendCommandResultAsync(command.Id, outcome.Ok, Clip(outcome.Message), cancellationToken)
                        .ConfigureAwait(false);
                ran++;
            }
            return ran;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "Falha ao buscar comandos do monitoramento");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Depois do reinício: diz ao painel se a atualização pedida por ele funcionou.</summary>
    public async Task ReportPendingUpdateAsync(CancellationToken cancellationToken = default)
    {
        var pending = fleet.GetPendingUpdate();
        if (pending is null) return;

        var current = SoftPrintVersion.Current;
        var ok = !string.Equals(
            SoftPrintVersionCompare.Normalize(pending.FromVersion),
            SoftPrintVersionCompare.Normalize(current),
            StringComparison.OrdinalIgnoreCase);
        var message = ok
            ? $"Atualizado de {pending.FromVersion} para {current}."
            : "A atualização não foi concluída. " +
              (UpdateFailureNotice.Peek() ?? $"O SoftPrint continua na versão {current}.");

        if (await fleet.SendCommandResultAsync(pending.CommandId, ok, Clip(message), cancellationToken).ConfigureAwait(false))
            fleet.ClearPendingUpdate();
    }

    private async Task<Outcome?> ExecuteAsync(FleetCommand command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.Type)
            {
                case "update":
                    if (!updates.TryStart(out var error))
                        return new Outcome(false, error ?? "Não foi possível iniciar a atualização.");
                    fleet.RememberPendingUpdate(command.Id);
                    return null; // resultado depois do reinício
                case "pause":
                    SetPaused(true);
                    return new Outcome(true, "Fila pausada.");
                case "resume":
                    SetPaused(false);
                    return new Outcome(true, "Fila retomada.");
                case "reprint_failed":
                    return ReprintFailed();
                case "test_print":
                    var job = GuidedTestPrint.Submit(jobs, settings, command.RequestedBy ?? "painel Softnew");
                    return new Outcome(true, $"Impressão de teste na fila ({job.Reference}).");
                case "support_package":
                    var path = support.CreateBundle();
                    var note = "Pedido pelo painel" + (command.RequestedBy is null ? "" : " por " + command.RequestedBy);
                    var sent = await fleet.UploadSupportPackageAsync(path, note, cancellationToken).ConfigureAwait(false);
                    return new Outcome(sent.Ok, sent.Message);
                case "refresh":
                    var hb = await fleet.SendHeartbeatAsync("heartbeat", cancellationToken).ConfigureAwait(false);
                    return new Outcome(hb.Ok, hb.Ok ? "Status enviado." : hb.Message);
                default:
                    return new Outcome(false, "Comando desconhecido: " + command.Type);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "Comando {Type} ({Id}) falhou", command.Type, command.Id);
            return new Outcome(false, ex.Message);
        }
    }

    private void SetPaused(bool paused)
    {
        for (var attempt = 0; ; attempt++)
        {
            var c = settings.Current;
            if (c.Paused == paused) return;
            try
            {
                settings.Update(c.PrinterName, c.Simulation, paused, c.ImageFit.ToWire(), c.ImageScalePercent,
                    c.PaperSize.ToWire(), c.PaperWidthMm, c.PaperHeightMm, c.PaperLandscape, c.Revision);
                return;
            }
            catch (SettingsConflictException) when (attempt == 0)
            {
                // Alguém salvou ao mesmo tempo: tenta de novo com a revisão nova.
            }
        }
    }

    private Outcome ReprintFailed()
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var failed = jobs.List()
            .Where(j => j.Status == JobStatus.Uncertain && (j.FinishedAt ?? j.CreatedAt) >= since)
            .OrderBy(j => j.FinishedAt ?? j.CreatedAt)
            .Take(MaxReprint)
            .ToList();
        var ok = 0;
        foreach (var job in failed)
        {
            try
            {
                jobs.Reprint(job.Id);
                ok++;
            }
            catch (ArgumentException ex)
            {
                log.LogDebug(ex, "Reimpressão de {Id} recusada", job.Id);
            }
        }
        return new Outcome(true, $"{ok} trabalho(s) reenviado(s).");
    }

    private bool Remember(long id)
    {
        lock (_done)
        {
            if (!_done.Add(id)) return false;
            _doneOrder.Enqueue(id);
            while (_doneOrder.Count > RememberIds) _done.Remove(_doneOrder.Dequeue());
            return true;
        }
    }

    private static string Clip(string message) =>
        message.Length <= MaxMessage ? message : message[..MaxMessage];
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 --filter "FullyQualifiedName~FleetCommandRunnerTests" 2>&1 | tail -1`
Expected: `Aprovado!` com 10 testes.

- [ ] **Step 5: Commit**

```bash
git add SoftPrint.Infrastructure.Core/Integrations/FleetCommandRunner.cs SoftPrint.Tests/FleetCommandRunnerTests.cs
git commit -m "feat: executor de comandos remotos do monitoramento"
```

### Task B6: Cliente WebSocket

**Files:**
- Create: `SoftPrint.Infrastructure.Core/Integrations/FleetRealtimeClient.cs`
- Create: `SoftPrint.Tests/FleetRealtimeClientTests.cs`

- [ ] **Step 1: Testes que falham**

`SoftPrint.Tests/FleetRealtimeClientTests.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using SoftPrint.Application.Abstractions;
using SoftPrint.Infrastructure.Integrations;
using Xunit;

namespace SoftPrint.Tests;

public sealed class FleetRealtimeClientTests
{
    [Theory]
    [InlineData("""{"v":1,"type":"softprint.command","room":"softprint:m:1","payload":{"id":5}}""", true)]
    [InlineData("""{"v":1,"type":"softprint.updated"}""", false)]
    [InlineData("""{"event":"ack"}""", false)]
    [InlineData("não é json", false)]
    public void IsCommandSignal(string json, bool expected) =>
        Assert.Equal(expected, FleetRealtimeClient.IsCommandSignal(json));

    [Fact]
    public void Backoff_GrowsAndCaps()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), FleetRealtimeClient.BackoffFor(0, jitter: false));
        Assert.Equal(TimeSpan.FromSeconds(15), FleetRealtimeClient.BackoffFor(2, jitter: false));
        Assert.Equal(TimeSpan.FromSeconds(60), FleetRealtimeClient.BackoffFor(10, jitter: false));
    }

    [Fact]
    public async Task Connects_Subscribes_AndPollsOnCommand()
    {
        string? auth = null;
        string? subscribe = null;
        var sendCommand = new TaskCompletionSource();
        using var server = new TestServer(new WebHostBuilder().Configure(app =>
        {
            app.UseWebSockets();
            app.Run(async ctx =>
            {
                auth = ctx.Request.Headers.Authorization.ToString();
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var buf = new byte[1024];
                var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                subscribe = Encoding.UTF8.GetString(buf, 0, r.Count);
                await sendCommand.Task;
                var msg = Encoding.UTF8.GetBytes("""{"v":1,"type":"softprint.command","payload":{"id":1}}""");
                await ws.SendAsync(msg, WebSocketMessageType.Text, true, CancellationToken.None);
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ContinueWith(_ => { });
            });
        }));

        var fleet = new RealtimeFleet();
        var poller = new CountingPoller();
        var client = new FleetRealtimeClient(fleet, poller, NullLogger<FleetRealtimeClient>.Instance)
        {
            StartDelay = TimeSpan.Zero,
            Connect = async (uri, token, ct) =>
            {
                var wsc = server.CreateWebSocketClient();
                wsc.ConfigureRequest = req => req.Headers.Authorization = "Bearer " + token;
                return await wsc.ConnectAsync(uri, ct);
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.StartAsync(cts.Token);
        await poller.WaitForAsync(1, cts.Token);          // busca ao conectar
        Assert.True(fleet.Connected);
        sendCommand.SetResult();
        await poller.WaitForAsync(2, cts.Token);          // busca pelo aviso
        await client.StopAsync(CancellationToken.None);

        Assert.Equal("Bearer jwt-teste", auth);
        using var doc = JsonDocument.Parse(subscribe!);
        Assert.Equal("subscribe", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("softprint:m:3", doc.RootElement.GetProperty("rooms")[0].GetString());
        Assert.False(fleet.Connected);
    }

    private sealed class CountingPoller : IFleetCommandPoller
    {
        private int _count;
        public Task<int> PollAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(0);
        }

        public async Task WaitForAsync(int count, CancellationToken ct)
        {
            while (Volatile.Read(ref _count) < count) await Task.Delay(20, ct);
        }
    }

    private sealed class RealtimeFleet : IFleetService
    {
        public bool Connected { get; private set; }
        public FleetStatus GetStatus() => new(true, false, "", "m1", "pc", "https://b.test", 40, null, DateTimeOffset.Now, true, null, null);
        public Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<FleetRealtimeTicket?>(new FleetRealtimeTicket(
                new Uri("ws://localhost/v1/ws"), "jwt-teste", "softprint:m:3", DateTimeOffset.UtcNow.AddHours(12)));
        public void SetRealtimeConnected(bool connected) => Connected = connected;
        public CancellationToken TokenChanged => CancellationToken.None;

        public void SetToken(string? token) { }
        public Task ChangeTokenAsync(string? token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default) => Task.FromResult(new FleetSendResult(true, ""));
        public Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default) => Task.FromResult(new FleetSendResult(true, ""));
        public Task<IReadOnlyList<FleetCommand>> GetCommandsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FleetCommand>>([]);
        public Task<bool> SendCommandResultAsync(long commandId, bool ok, string message, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void NoteCommand(string description) { }
        public void RememberPendingUpdate(long commandId) { }
        public FleetPendingUpdate? GetPendingUpdate() => null;
        public void ClearPendingUpdate() { }
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 --filter "FullyQualifiedName~FleetRealtimeClientTests" 2>&1 | grep -E "error CS" | head -2`
Expected: `error CS0246: … 'FleetRealtimeClient' could not be found`

- [ ] **Step 3: Implementar**

`SoftPrint.Infrastructure.Core/Integrations/FleetRealtimeClient.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Integrations;

/// <summary>
/// WebSocket com o hub da Softnew: só recebe avisos de "comando novo" na sala desta máquina.
/// O comando em si é buscado por HTTPS (<see cref="IFleetCommandPoller"/>). Sem conexão, o heartbeat
/// cobre a busca a cada 15 s.
/// </summary>
public sealed class FleetRealtimeClient(
    IFleetService fleet,
    IFleetCommandPoller poller,
    ILogger<FleetRealtimeClient> log) : BackgroundService
{
    private static readonly int[] BackoffSeconds = [2, 5, 15, 30, 60];
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(30);

    internal TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Abre a conexão (trocável nos testes).</summary>
    internal Func<Uri, string, CancellationToken, Task<WebSocket>> Connect { get; init; } = DefaultConnectAsync;

    internal static bool IsCommandSignal(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("type", out var t)
                   && t.ValueKind == JsonValueKind.String
                   && t.GetString() == "softprint.command";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static TimeSpan BackoffFor(int failures, bool jitter = true)
    {
        var seconds = BackoffSeconds[Math.Min(failures, BackoffSeconds.Length - 1)];
#if NET6_0_OR_GREATER
        var r = Random.Shared.NextDouble();
#else
        var r = new Random().NextDouble();
#endif
        var extra = jitter ? r * seconds * 0.2 : 0;
        return TimeSpan.FromSeconds(seconds + extra);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = fleet.GetStatus();
            // Só depois do primeiro heartbeat com sucesso a máquina existe no backend.
            if (!status.Configured || status.LastSuccessAt is null)
            {
                if (!await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken)) return;
                continue;
            }

            var tokenChanged = fleet.TokenChanged;
            try
            {
                var ticket = await fleet.GetRealtimeTicketAsync(stoppingToken).ConfigureAwait(false);
                if (ticket is null)
                {
                    // Servidor sem tempo real, ou recusou: tenta de novo bem mais tarde.
                    failures++;
                    var wait = failures >= 3 ? TimeSpan.FromMinutes(5) : BackoffFor(failures);
                    if (!await DelayAsync(wait, stoppingToken)) return;
                    continue;
                }

                await RunConnectionAsync(ticket, tokenChanged, stoppingToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                log.LogDebug(ex, "Tempo real do monitoramento caiu");
            }
            finally
            {
                fleet.SetRealtimeConnected(false);
            }

            if (failures > 0 && !await DelayAsync(BackoffFor(failures - 1), stoppingToken)) return;
        }
    }

    private async Task RunConnectionAsync(FleetRealtimeTicket ticket, CancellationToken tokenChanged, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, tokenChanged);
        var renewIn = ticket.ExpiresAt - DateTimeOffset.UtcNow - RenewBefore;
        if (renewIn > TimeSpan.Zero) linked.CancelAfter(renewIn);
        var ct = linked.Token;

        using var ws = await Connect(ticket.WsUrl, ticket.Token, ct).ConfigureAwait(false);
        var subscribe = JsonSerializer.Serialize(new { action = "subscribe", rooms = new[] { ticket.Room } });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(subscribe)), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        fleet.SetRealtimeConnected(true);
        log.LogInformation("Tempo real do monitoramento conectado");
        _ = PollSafeAsync(stoppingToken); // pega o que chegou enquanto estava desconectado

        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (message.Length <= 64 * 1024 && IsCommandSignal(Encoding.UTF8.GetString(message.ToArray())))
                    _ = PollSafeAsync(stoppingToken);
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Renovação do token ou token trocado: reconecta na próxima volta.
        }

        if (ws.State == WebSocketState.Open)
        {
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
            catch { /* já caiu */ }
        }
    }

    private async Task PollSafeAsync(CancellationToken ct)
    {
        try { await poller.PollAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogDebug(ex, "Busca de comandos falhou"); }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<WebSocket> DefaultConnectAsync(Uri uri, string token, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 --filter "FullyQualifiedName~FleetRealtimeClientTests" 2>&1 | tail -1`
Expected: `Aprovado!` com 6 testes.

- [ ] **Step 5: Compilar o Legacy**

Run: `dotnet build SoftPrint.Host.Windows.Legacy/SoftPrint.Host.Windows.Legacy.csproj -c Release 2>&1 | grep -E "Erro\(s\)|error CS"`
Expected: `0 Erro(s)`

- [ ] **Step 6: Commit**

```bash
git add SoftPrint.Infrastructure.Core/Integrations/FleetRealtimeClient.cs SoftPrint.Tests/FleetRealtimeClientTests.cs
git commit -m "feat: WebSocket do monitoramento avisa comandos novos"
```

### Task B7: Heartbeat — 15 s sem WebSocket, status na hora e comandos

**Files:**
- Modify: `SoftPrint.Infrastructure.Core/Operations/FleetHeartbeatHostedService.cs`
- Modify: `SoftPrint.Tests/FleetServiceTests.cs`

- [ ] **Step 1: Teste da regra (falha)**

Acrescente em `FleetServiceTests.cs`, antes de `private FleetService Create(`:

```csharp
    [Theory]
    // agora=100s; último envio; mudança em; intervalo; esperado
    [InlineData(100, 59, null, 40, true)]   // intervalo venceu
    [InlineData(100, 70, null, 40, false)]  // dentro do intervalo, sem mudança
    [InlineData(100, 90, 97, 40, true)]     // mudou há 3 s e o último envio foi há 10 s
    [InlineData(100, 90, 99, 40, false)]    // mudou há 1 s: espera juntar mudanças (2 s)
    [InlineData(100, 97, 95, 40, false)]    // último envio há 3 s: respeita 5 s
    [InlineData(100, 86, null, 15, true)]   // sem WebSocket: 15 s
    public void HeartbeatSchedule(int now, int lastSent, int? changedAt, int interval, bool expected)
    {
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.Equal(expected, SoftPrint.Infrastructure.Operations.FleetHeartbeatHostedService.ShouldSend(
            t0.AddSeconds(now), t0.AddSeconds(lastSent),
            changedAt is null ? null : t0.AddSeconds(changedAt.Value),
            TimeSpan.FromSeconds(interval)));
    }
```

Run: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 --filter "FullyQualifiedName~HeartbeatSchedule" 2>&1 | grep -E "error CS" | head -1`
Expected: `error CS0117: 'FleetHeartbeatHostedService' does not contain a definition for 'ShouldSend'`

- [ ] **Step 2: Reescrever o serviço**

Troque o conteúdo de `FleetHeartbeatHostedService.cs` por:

```csharp
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Operations;

/// <summary>
/// Heartbeat para o monitoramento Softnew. O servidor marca a máquina offline depois de
/// ~2 min sem contato; ao fechar normalmente, avisa "stopping" para não gerar alerta.
/// Com o WebSocket conectado, o intervalo é o configurado; sem ele, 15 s (é quando os comandos
/// chegam por aqui). Mudou a fila? Manda na hora (2 s para juntar mudanças, no máximo um a cada 5 s).
/// </summary>
public sealed class FleetHeartbeatHostedService(
    IFleetService fleet,
    FleetCommandRunner runner,
    JobQueueService jobs,
    ISettingsRepository settings,
    IOptions<SoftPrintFeatureOptions> options,
    ILogger<FleetHeartbeatHostedService> log) : BackgroundService
{
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);

    private volatile bool _announced;

    public static bool ShouldSend(DateTimeOffset now, DateTimeOffset? lastSent, DateTimeOffset? changedAt, TimeSpan interval)
    {
        if (lastSent is null) return true;
        var sinceSent = now - lastSent.Value;
        if (sinceSent >= interval) return true;
        return changedAt is not null && now - changedAt.Value >= Settle && sinceSent >= MinGap;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        DateTimeOffset? lastSent = null;
        DateTimeOffset? changedAt = null;
        string? lastFingerprint = null;
        var reportedUpdate = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var status = fleet.GetStatus();
            if (status.Configured)
            {
                var now = DateTimeOffset.UtcNow;
                var fingerprint = Fingerprint();
                if (lastFingerprint is not null && fingerprint != lastFingerprint && changedAt is null)
                    changedAt = now;
                lastFingerprint = fingerprint;

                var configured = TimeSpan.FromSeconds(FleetService.HeartbeatSeconds(options.Value));
                var interval = status.RealtimeConnected ? configured : (configured < FallbackInterval ? configured : FallbackInterval);

                if (ShouldSend(now, lastSent, changedAt, interval))
                {
                    lastSent = now;
                    changedAt = null;
                    try
                    {
                        var result = await fleet.SendHeartbeatAsync("heartbeat", stoppingToken).ConfigureAwait(false);
                        if (result.Ok)
                        {
                            _announced = true;
                            if (!reportedUpdate)
                            {
                                reportedUpdate = true;
                                await runner.ReportPendingUpdateAsync(stoppingToken).ConfigureAwait(false);
                            }
                            if (result.PendingCommands > 0)
                                await runner.PollAsync(stoppingToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        log.LogDebug(ex, "Heartbeat do monitoramento falhou");
                    }
                }
            }

            try { await Task.Delay(Tick, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    /// <summary>O que o painel mostra da fila: mudou, manda heartbeat.</summary>
    private string Fingerprint()
    {
        try
        {
            var all = jobs.List();
            var pending = all.Count(j => j.Status == JobStatus.Pending);
            var processing = all.Count(j => j.Status == JobStatus.Processing);
            var lastError = all.Where(j => j.Status == JobStatus.Uncertain)
                .OrderByDescending(j => j.FinishedAt).Select(j => j.Id).FirstOrDefault();
            return $"{pending}|{processing}|{lastError}|{settings.Current.Paused}";
        }
        catch
        {
            return "";
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!_announced || !fleet.GetStatus().Configured) return;

        // Fechamento normal: o painel mostra "parado" em vez de alertar queda.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await fleet.SendHeartbeatAsync("stopping", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Aviso de encerramento ao monitoramento falhou");
        }
    }
}
```

- [ ] **Step 3: Registro no DI**

Em `SoftPrintHostExtensions.AddSoftPrintCore`, depois de `services.AddSingleton<IFleetService, FleetService>();`:

```csharp
        services.AddSingleton<FleetCommandRunner>();
        services.AddSingleton<IFleetCommandPoller>(sp => sp.GetRequiredService<FleetCommandRunner>());
```

e depois de `services.AddHostedService<SoftPrint.Infrastructure.Operations.FleetHeartbeatHostedService>();`:

```csharp
        services.AddHostedService<FleetRealtimeClient>();
```

- [ ] **Step 4: Rodar tudo**

Run: `dotnet test SoftPrint.Tests/SoftPrint.Tests.csproj -f net10.0 2>&1 | tail -1`
Expected: `Aprovado!` (87 antigos + os novos, nenhuma falha).

Run: `dotnet build SoftPrint.Host.Windows.Legacy/SoftPrint.Host.Windows.Legacy.csproj -c Release 2>&1 | grep -E "Erro\(s\)"` e `dotnet build SoftPrint.Tests/SoftPrint.Tests.csproj -f net6.0 2>&1 | grep -E "Erro\(s\)"`
Expected: `0 Erro(s)` nos dois.

- [ ] **Step 5: Commit**

```bash
git add SoftPrint.Infrastructure.Core/Operations/FleetHeartbeatHostedService.cs SoftPrint.Infrastructure.Core/Hosting/SoftPrintHostExtensions.cs SoftPrint.Tests/FleetServiceTests.cs
git commit -m "feat: heartbeat na hora quando a fila muda e a cada 15 s sem tempo real"
```

### Task B8: Tela Sistema mostra o tempo real

**Files:**
- Modify: `SoftPrint.Host.Windows/wwwroot/js/components/system-settings-tab.js` (`renderFleet`)
- Regenerate: `SoftPrint.Host.Windows/wwwroot/js/app.bundle.js`

- [ ] **Step 1: Texto**

Em `renderFleet`, troque a linha

```js
      text = `Conectado${status.empresa ? ` · ${status.empresa}` : ""}${when ? ` · último contato ${when}` : ""}`;
```

por:

```js
      const realtime = status.realtimeConnected ? "tempo real ligado" : "comandos a cada 15 s";
      const last = status.lastCommand ? ` · último comando ${status.lastCommand}` : "";
      text = `Conectado${status.empresa ? ` · ${status.empresa}` : ""}${when ? ` · último contato ${when}` : ""} · ${realtime}${last}`;
```

- [ ] **Step 2: Gerar o bundle**

Run (PowerShell): `npx --yes esbuild SoftPrint.Host.Windows/wwwroot/js/app.js --bundle --format=iife --target=chrome109 --outfile=SoftPrint.Host.Windows/wwwroot/js/app.bundle.js`
Expected: `app.bundle.js` gerado sem erro. Confira `git diff --stat` — se o bundle mudar só fim de linha, desfaça com `git checkout -- SoftPrint.Host.Windows/wwwroot/js/app.bundle.js` e gere de novo.

- [ ] **Step 3: Commit**

```bash
git add SoftPrint.Host.Windows/wwwroot/js/components/system-settings-tab.js SoftPrint.Host.Windows/wwwroot/js/app.bundle.js
git commit -m "feat: tela Sistema mostra tempo real e último comando"
```

---

## Parte C — conex_database (painel)

Trabalhe em `C:\Users\pc\Desktop\mednews\mednews_web\conex_database`.

### Task C1: Store e regras dos comandos

**Files:**
- Modify: `src/stores/softprint.js`
- Create: `src/utils/softprintCommands.js`
- Create: `src/utils/softprintCommands.test.js`

- [ ] **Step 1: Branch**

```bash
cd /c/Users/pc/Desktop/mednews/mednews_web/conex_database
git checkout main && git pull --ff-only origin main
git checkout -b feat/softprint-comandos
```

- [ ] **Step 2: Teste que falha**

O projeto usa vitest com os testes ao lado do código (`src/**/*.test.js`). Crie `src/utils/softprintCommands.test.js`:

```js
import { describe, expect, it } from 'vitest'
import { COMMANDS, commandLabel, statusLabel, needsConfirm, availableCommands } from './softprintCommands'

describe('comandos do SoftPrint', () => {
  it('lista os sete comandos', () => {
    expect(COMMANDS.map(c => c.type))
      .toEqual(['update', 'pause', 'resume', 'reprint_failed', 'test_print', 'support_package', 'refresh'])
  })

  it('rótulos', () => {
    expect(commandLabel('pause')).toBe('Pausar fila')
    expect(commandLabel('xyz')).toBe('xyz')
    expect(statusLabel('delivered')).toBe('entregue')
    expect(statusLabel('expired')).toBe('expirado')
  })

  it('atualizar e pausar pedem confirmação', () => {
    expect(needsConfirm('update')).toBe(true)
    expect(needsConfirm('pause')).toBe(true)
    expect(needsConfirm('refresh')).toBe(false)
  })

  it('mostra pausar ou retomar conforme a fila', () => {
    const ativos = availableCommands({ paused: 0 }).map(c => c.type)
    expect(ativos).toContain('pause')
    expect(ativos).not.toContain('resume')
    const pausada = availableCommands({ paused: 1 }).map(c => c.type)
    expect(pausada).toContain('resume')
    expect(pausada).not.toContain('pause')
  })
})
```

Run: `npx vitest run src/utils/softprintCommands.test.js 2>&1 | tail -5`
Expected: falha com `Failed to resolve import "./softprintCommands"`.

- [ ] **Step 3: Implementar**

`src/utils/softprintCommands.js`:

```js
/** Comandos remotos do SoftPrint: rótulos e regras da tela. */
export const COMMANDS = [
  { type: 'update', label: 'Atualizar SoftPrint', confirm: 'Atualizar o SoftPrint desta máquina agora? Ele reinicia sozinho.' },
  { type: 'pause', label: 'Pausar fila', confirm: 'Pausar a fila de impressão desta máquina?' },
  { type: 'resume', label: 'Retomar fila' },
  { type: 'reprint_failed', label: 'Reimprimir com erro' },
  { type: 'test_print', label: 'Impressão de teste' },
  { type: 'support_package', label: 'Pedir pacote de suporte' },
  { type: 'refresh', label: 'Atualizar status' },
]

const STATUS = {
  pending: 'enviado',
  delivered: 'entregue',
  done: 'concluído',
  failed: 'falhou',
  expired: 'expirado',
}

export const commandLabel = type => COMMANDS.find(c => c.type === type)?.label ?? type
export const statusLabel = status => STATUS[status] ?? status
export const needsConfirm = type => Boolean(COMMANDS.find(c => c.type === type)?.confirm)
export const confirmText = type => COMMANDS.find(c => c.type === type)?.confirm ?? ''

/** Pausar ou Retomar conforme o estado da fila; o resto sempre. */
export function availableCommands(machine) {
  const paused = Number(machine?.paused) === 1
  return COMMANDS.filter(c => (c.type === 'pause' ? !paused : c.type === 'resume' ? paused : true))
}
```

- [ ] **Step 4: Store**

Em `src/stores/softprint.js`, acrescente antes do `return {`:

```js
  const commandsByMachine = ref({})

  async function sendCommand(machineId, type) {
    const json = await apiFetch(`/api/softprint/machines/${machineId}/commands`, {
      method: 'POST',
      body: JSON.stringify({ type }),
    })
    await fetchCommands(machineId)
    return json.data
  }

  async function fetchCommands(machineId) {
    const json = await apiFetch(`/api/softprint/machines/${machineId}/commands?limit=50`)
    commandsByMachine.value = { ...commandsByMachine.value, [machineId]: json.data ?? [] }
    return commandsByMachine.value[machineId]
  }
```

e acrescente `commandsByMachine, sendCommand, fetchCommands,` à lista do `return`.

- [ ] **Step 5: Rodar**

Run: `npx vitest run src/utils/softprintCommands.test.js 2>&1 | tail -4`
Expected: `4 passed`.

- [ ] **Step 6: Commit**

```bash
git add src/utils/softprintCommands.js src/stores/softprint.js src/utils/softprintCommands.test.js
git commit -m "feat(softprint): comandos remotos na store"
```

### Task C2: Menu de ações e histórico

**Files:**
- Create: `src/components/SoftPrintCommandMenu.vue`
- Create: `src/components/SoftPrintCommandHistory.vue`
- Modify: `src/components/SoftPrintView.vue`

- [ ] **Step 1: Menu**

`src/components/SoftPrintCommandMenu.vue`:

```vue
<script setup>
import { ref, computed, inject } from 'vue'
import { useSoftprintStore } from '../stores/softprint'
import { availableCommands, confirmText, needsConfirm } from '../utils/softprintCommands'

const props = defineProps({ machine: { type: Object, required: true } })
const emit = defineEmits(['history'])
const store = useSoftprintStore()
const showToast = inject('showToast', () => {})
const open = ref(false)
const busy = ref(false)
const options = computed(() => availableCommands(props.machine))

async function run(cmd) {
  open.value = false
  if (needsConfirm(cmd.type) && !confirm(confirmText(cmd.type))) return
  if (props.machine.status !== 'online'
      && !confirm('A máquina não está online. O comando será entregue quando ela voltar, se for em até 10 min. Enviar?')) return
  busy.value = true
  try {
    await store.sendCommand(props.machine.id, cmd.type)
    showToast(`${cmd.label}: enviado para ${props.machine.hostname || 'a máquina'}`)
  } catch (e) {
    showToast(e.message, 'error')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="relative inline-block text-left" @keydown.escape="open = false">
    <button type="button" :disabled="busy" @click="open = !open" :aria-expanded="open"
            class="text-xs font-medium text-blue-600 hover:underline disabled:opacity-40">
      Ações
    </button>
    <div v-if="open" class="absolute right-0 z-20 mt-1 w-56 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 shadow-lg py-1">
      <button v-for="cmd in options" :key="cmd.type" type="button" @click="run(cmd)"
              class="block w-full text-left px-3 py-1.5 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-800">
        {{ cmd.label }}
      </button>
      <div class="border-t border-gray-100 dark:border-gray-800 my-1" />
      <button type="button" @click="open = false; emit('history', machine)"
              class="block w-full text-left px-3 py-1.5 text-sm text-gray-500 hover:bg-gray-50 dark:hover:bg-gray-800">
        Histórico de comandos
      </button>
    </div>
  </div>
</template>
```

- [ ] **Step 2: Histórico**

`src/components/SoftPrintCommandHistory.vue`:

```vue
<script setup>
import { computed, onMounted, watch } from 'vue'
import { useSoftprintStore } from '../stores/softprint'
import { commandLabel, statusLabel } from '../utils/softprintCommands'

const props = defineProps({ machine: { type: Object, required: true } })
const emit = defineEmits(['close'])
const store = useSoftprintStore()
const rows = computed(() => store.commandsByMachine[props.machine.id] ?? [])

const TONE = {
  done: 'text-emerald-600 dark:text-emerald-400',
  failed: 'text-red-600 dark:text-red-400',
  expired: 'text-gray-400',
  pending: 'text-amber-600',
  delivered: 'text-blue-600',
}

function formatDate(val) {
  return val ? new Date(String(val).replace(' ', 'T')).toLocaleString('pt-BR') : '—'
}

const load = () => store.fetchCommands(props.machine.id).catch(() => {})
onMounted(load)
watch(() => store.fetchedAt, load) // recarrega quando chega softprint.updated
</script>

<template>
  <div class="fixed inset-0 z-40 flex justify-end bg-black/30" @click.self="emit('close')">
    <aside class="w-full max-w-md h-full bg-white dark:bg-gray-900 shadow-xl flex flex-col" role="dialog"
           :aria-label="`Comandos de ${machine.hostname}`">
      <header class="px-5 py-4 border-b border-gray-100 dark:border-gray-800 flex items-center">
        <div>
          <h3 class="font-semibold text-gray-900 dark:text-gray-100">Comandos</h3>
          <p class="text-xs text-gray-400">{{ machine.EMP_FANTASIA }} · {{ machine.hostname }}</p>
        </div>
        <div class="flex-1" />
        <button type="button" @click="emit('close')" class="text-sm text-gray-500 hover:text-gray-900">Fechar</button>
      </header>
      <ul class="flex-1 overflow-auto divide-y divide-gray-50 dark:divide-gray-800">
        <li v-for="c in rows" :key="c.id" class="px-5 py-3 text-sm">
          <div class="flex items-center gap-2">
            <span class="font-medium text-gray-800 dark:text-gray-200">{{ commandLabel(c.type) }}</span>
            <span :class="['text-xs', TONE[c.status]]">{{ statusLabel(c.status) }}</span>
          </div>
          <div v-if="c.result_message" class="text-xs text-gray-600 dark:text-gray-300 mt-0.5">{{ c.result_message }}</div>
          <div class="text-xs text-gray-400 mt-0.5">{{ c.requested_by || '—' }} · {{ formatDate(c.created_at) }}</div>
        </li>
        <li v-if="!rows.length" class="px-5 py-10 text-center text-sm text-gray-400">Nenhum comando ainda.</li>
      </ul>
    </aside>
  </div>
</template>
```

- [ ] **Step 3: Usar na tela**

Em `SoftPrintView.vue`:

3a. Imports no `<script setup>`:

```js
import SoftPrintCommandMenu from './SoftPrintCommandMenu.vue'
import SoftPrintCommandHistory from './SoftPrintCommandHistory.vue'
```

e, depois de `const now = ref(Date.now())`:

```js
const historyFor = ref(null)
```

3b. Coluna Fila: troque

```html
              <span>{{ m.pending }} na fila · {{ m.processing }} enviando</span>
```

por

```html
              <span>{{ m.pending }} na fila · {{ m.processing }} enviando</span>
              <span v-if="m.paused" class="ml-1 rounded bg-amber-100 dark:bg-amber-900/30 text-amber-700 dark:text-amber-300 px-1.5 py-0.5">pausada</span>
```

3c. Coluna de ações da máquina: troque

```html
              <button @click="packagesOf(m)" class="text-xs text-blue-600 hover:underline mr-3">Pacotes</button>
```

por

```html
              <span v-if="m.pending_commands" class="mr-3 text-xs text-amber-600" :title="'Comandos aguardando a máquina'">
                {{ m.pending_commands }} comando(s)
              </span>
              <SoftPrintCommandMenu v-if="isAdmin" :machine="m" class="mr-3" @history="historyFor = $event" />
              <button @click="packagesOf(m)" class="text-xs text-blue-600 hover:underline mr-3">Pacotes</button>
```

3d. Antes do `</template>` final do componente (depois do último `</div>` do conteúdo), acrescente:

```html
    <SoftPrintCommandHistory v-if="historyFor" :machine="historyFor" @close="historyFor = null" />
```

(dentro do `<div>` raiz, para o template continuar com um único elemento raiz).

- [ ] **Step 4: Build e testes**

Run: `npm run build 2>&1 | tail -2 && npm test 2>&1 | tail -8`
Expected: `✓ built in …` e nenhum teste falhando (vitest e `node --test`).

- [ ] **Step 5: Commit**

```bash
git add src/components/SoftPrintCommandMenu.vue src/components/SoftPrintCommandHistory.vue src/components/SoftPrintView.vue
git commit -m "feat(softprint): ações e histórico de comandos por máquina"
```

---

## Parte D — Ponta a ponta e entrega

### Task D1: Teste ponta a ponta local

- [ ] **Step 1: Subir MariaDB e o hub em Docker**

```bash
docker run -d --name sp-e2e-db -e MARIADB_ROOT_PASSWORD=e2e -e MARIADB_DATABASE=e2e -p 33061:3306 mariadb:10.6
cd /c/Users/pc/Desktop/mednews/mednews_web/servidor_websocket && docker compose up -d
```

Crie a tabela `EMPRESAS` mínima e uma empresa de teste no banco `e2e` (`CREATE TABLE EMPRESAS (EMP_CODIGO INT PRIMARY KEY, EMP_FANTASIA VARCHAR(120)); INSERT INTO EMPRESAS VALUES (1,'Clínica Teste');`).

- [ ] **Step 2: Backend local**

Num `.env` só local do backendempresa (não commitar), aponte `DB_*` para `127.0.0.1:33061`, `REALTIME_BASE_URL`/`REALTIME_API_KEY`/`REALTIME_JWT_SECRET` para o hub local e rode `php -S 127.0.0.1:8099 index.php`. Gere um token da empresa 1 com `POST /api/softprint/empresas/1/token` (login de painel local).

- [ ] **Step 3: SoftPrint Debug**

Rode o SoftPrint (Debug) com `FLEET_URL=http://127.0.0.1:8099` e o token. Confirme na tela Sistema: "Conectado · Clínica Teste · tempo real ligado".

- [ ] **Step 4: Comandos**

Pelo painel local (ou `curl` com o token do painel), mande `pause`, `resume`, `test_print`, `refresh`, `support_package` e `update`. Confira para cada um: resultado em menos de 3 s com o WebSocket ligado; `done`/`failed` com a mensagem certa no histórico.

- [ ] **Step 5: Sem WebSocket**

Pare o hub (`docker compose stop`). Confirme "comandos a cada 15 s" na tela Sistema e que um `refresh` chega em até ~15 s.

- [ ] **Step 6: Expiração**

Feche o SoftPrint, mande um `refresh`, rode o cron do backend com `SOFTPRINT_COMMAND_TTL_SECONDS=60` depois de 1 min e confirme `expired`.

- [ ] **Step 7: Limpar**

```bash
docker rm -f sp-e2e-db
cd /c/Users/pc/Desktop/mednews/mednews_web/servidor_websocket && docker compose down
```

### Task D2: PRs, merge e release

- [ ] **Step 1: PRs** — um por repositório (backendempresa → `master`, conex_database → `main`, SoftPrint → `main`), cada um com o que muda, como foi testado e o que não foi (WebSocket no Windows 7 real e UAC numa atualização real).
- [ ] **Step 2: Merge na ordem** backendempresa (deploy; o SoftPrint antigo ignora os campos novos) → conex_database → SoftPrint.
- [ ] **Step 3: Conferir em produção** que `GET /api/softprint/agent/realtime-token` responde 200 (e não 503) nos dois servidores — senão faltam as variáveis `REALTIME_*` em algum deles.
- [ ] **Step 4: Release** 1.0.42 pelo workflow `release.yml` (patch).
