using BackupMonitorApi.Models;
using Dapper;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = false;
SqlMapper.AddTypeMap(typeof(DateTime),  System.Data.DbType.DateTime2);
SqlMapper.AddTypeMap(typeof(DateTime?), System.Data.DbType.DateTime2);

var app = builder.Build();
app.UseCors();
app.UseStaticFiles();

// ── Conexão ───────────────────────────────────────────────────────────────────
var rawUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
          ?? app.Configuration.GetConnectionString("DefaultConnection")
          ?? throw new Exception("DATABASE_URL nao configurada.");

var connStr = ConvertUrl(rawUrl);

static string ConvertUrl(string url)
{
    if (!url.StartsWith("postgresql://") && !url.StartsWith("postgres://"))
        return url;
    var uri    = new Uri(url);
    var info   = uri.UserInfo.Split(':', 2);
    var user   = Uri.UnescapeDataString(info[0]);
    var pass   = info.Length > 1 ? Uri.UnescapeDataString(info[1]) : "";
    var host   = uri.Host;
    var dbPort = uri.Port > 0 ? uri.Port : 5432;
    var db     = uri.AbsolutePath.TrimStart('/');
    return $"Host={host};Port={dbPort};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true;";
}

var apiKey = Environment.GetEnvironmentVariable("API_KEY") ?? "dev-key-local";

// ── Migração automática ───────────────────────────────────────────────────────
await using (var conn = new NpgsqlConnection(connStr))
{
    await conn.OpenAsync();

    // Cria tabela com todos os campos (incluindo os novos)
    await conn.ExecuteAsync(@"
        CREATE TABLE IF NOT EXISTS ""BackupLogs"" (
            ""Id""                SERIAL PRIMARY KEY,
            ""DataExecucao""      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            ""ClienteNome""       TEXT,
            ""ClienteCNPJ""       TEXT,
            ""BancoNome""         TEXT,
            ""TipoBackup""        TEXT,
            ""Status""            TEXT,
            ""NomeArquivo""       TEXT,
            ""Ciclo""             TEXT,
            ""Servidor""          TEXT,
            ""Edicao""            TEXT,
            ""Versao""            TEXT,
            ""Recovery""          TEXT,
            ""TamanhoDadosGB""    NUMERIC(10,3) DEFAULT 0,
            ""TamanhoLogGB""      NUMERIC(10,3) DEFAULT 0,
            ""PercentualExpress"" NUMERIC(5,2)  DEFAULT 0,
            ""StatusLimite""      TEXT,
            ""IntervalHoras""     INTEGER       DEFAULT 0,
            ""ProximaExecucao""   TIMESTAMPTZ,
            ""Estrategia""        TEXT          DEFAULT 'Simple',
            ""TipoOperacao""      TEXT          DEFAULT 'Diferencial'
        );
        CREATE INDEX IF NOT EXISTS idx_bl_banco ON ""BackupLogs""(""BancoNome"");
        CREATE INDEX IF NOT EXISTS idx_bl_data  ON ""BackupLogs""(""DataExecucao"" DESC);
        CREATE INDEX IF NOT EXISTS idx_bl_tipo  ON ""BackupLogs""(""TipoBackup"");
        CREATE INDEX IF NOT EXISTS idx_bl_cli   ON ""BackupLogs""(""ClienteCNPJ"");
    ");

    // Migration segura para tabelas já existentes — nunca perde dados
    await conn.ExecuteAsync(@"
        ALTER TABLE ""BackupLogs""
            ADD COLUMN IF NOT EXISTS ""IntervalHoras""   INTEGER     DEFAULT 0,
            ADD COLUMN IF NOT EXISTS ""ProximaExecucao"" TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS ""Estrategia""      TEXT        DEFAULT 'Simple',
            ADD COLUMN IF NOT EXISTS ""TipoOperacao""    TEXT        DEFAULT 'Diferencial';
    ");
}

// ════════════════════════════════════════════════════════════════════════════
// POST /api/backup — recebe evento detalhado do TelegramClient.cs
// Header: X-Api-Key
// ════════════════════════════════════════════════════════════════════════════
app.MapPost("/api/backup", async (HttpContext ctx, BackupLog log) =>
{
    if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) || key != apiKey)
        return Results.Unauthorized();

    await using var conn = new NpgsqlConnection(connStr);
    await conn.ExecuteAsync(@"
        INSERT INTO ""BackupLogs"" (
            ""DataExecucao"", ""ClienteNome"", ""ClienteCNPJ"",
            ""BancoNome"", ""TipoBackup"", ""Status"", ""NomeArquivo"", ""Ciclo"",
            ""Servidor"", ""Edicao"", ""Versao"", ""Recovery"",
            ""TamanhoDadosGB"", ""TamanhoLogGB"", ""PercentualExpress"", ""StatusLimite"",
            ""IntervalHoras"", ""ProximaExecucao"",
            ""Estrategia"", ""TipoOperacao""
        ) VALUES (
            @DataExecucao, @ClienteNome, @ClienteCNPJ,
            @BancoNome, @TipoBackup, @Status, @NomeArquivo, @Ciclo,
            @Servidor, @Edicao, @Versao, @Recovery,
            @TamanhoDadosGB, @TamanhoLogGB, @PercentualExpress, @StatusLimite,
            @IntervalHoras, @ProximaExecucao,
            @Estrategia, @TipoOperacao
        )", log);

    return Results.Ok(new { message = "Backup registrado com sucesso" });
});

// ════════════════════════════════════════════════════════════════════════════
// POST /evento — recebe evento de status em tempo real do MonitorClient.cs
// Payload: { cliente, banco, estado, mensagem, ciclo, tamanho,
//            intervalHoras, proximaExecucao, alertaCiclo,
//            estrategia, tipoOperacao }
// ════════════════════════════════════════════════════════════════════════════
app.MapPost("/evento", async (HttpContext ctx) =>
{
    if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) || key != apiKey)
        return Results.Unauthorized();

    var evt = await ctx.Request.ReadFromJsonAsync<EventoMonitor>();
    if (evt is null) return Results.BadRequest("Payload inválido.");

    await using var conn = new NpgsqlConnection(connStr);
    await conn.ExecuteAsync(@"
        INSERT INTO ""BackupLogs"" (
            ""DataExecucao"", ""ClienteNome"",
            ""BancoNome"", ""TipoBackup"", ""Status"",
            ""Ciclo"",
            ""IntervalHoras"", ""ProximaExecucao"",
            ""Estrategia"", ""TipoOperacao""
        ) VALUES (
            NOW(), @Cliente,
            @Banco, @Estado, @Mensagem,
            @Ciclo,
            @IntervalHoras, @ProximaExecucao,
            @Estrategia, @TipoOperacao
        )", new
    {
        evt.Cliente,
        evt.Banco,
        Estado           = evt.Estado,
        Mensagem         = evt.Mensagem,
        Ciclo            = evt.Ciclo,
        IntervalHoras    = evt.IntervalHoras,
        ProximaExecucao  = evt.ProximaExecucao,
        Estrategia       = evt.Estrategia ?? "Simple",
        TipoOperacao     = evt.TipoOperacao ?? "Diferencial",
    });

    return Results.Ok(new { message = "Evento registrado" });
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/historico?limit=100&banco=food&tipo=Erro&cliente=ABC&de=2026-01-01&ate=2026-03-31
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/historico", async (
    int limit          = 100,
    string? banco      = null,
    string? tipo       = null,
    string? cliente    = null,
    string? estrategia = null,
    string? tipoOp     = null,
    string? de         = null,
    string? ate        = null) =>
{
    await using var conn = new NpgsqlConnection(connStr);

    var where = new List<string>();
    var prms  = new DynamicParameters();

    if (!string.IsNullOrWhiteSpace(banco))      { where.Add(@"""BancoNome"" = @banco");      prms.Add("banco",      banco); }
    if (!string.IsNullOrWhiteSpace(tipo))       { where.Add(@"""TipoBackup"" = @tipo");      prms.Add("tipo",       tipo); }
    if (!string.IsNullOrWhiteSpace(cliente))    { where.Add(@"""ClienteNome"" ILIKE @cli");  prms.Add("cli",        $"%{cliente}%"); }
    if (!string.IsNullOrWhiteSpace(estrategia)) { where.Add(@"""Estrategia"" = @est");       prms.Add("est",        estrategia); }
    if (!string.IsNullOrWhiteSpace(tipoOp))     { where.Add(@"""TipoOperacao"" = @tipoOp");  prms.Add("tipoOp",     tipoOp); }
    if (!string.IsNullOrWhiteSpace(de))         { where.Add(@"""DataExecucao"" >= @de");     prms.Add("de",         DateTime.Parse(de)); }
    if (!string.IsNullOrWhiteSpace(ate))        { where.Add(@"""DataExecucao"" <= @ate");    prms.Add("ate",        DateTime.Parse(ate + " 23:59:59")); }
    prms.Add("limit", Math.Min(limit, 1000));

    var sql = $@"SELECT * FROM ""BackupLogs""
                 {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
                 ORDER BY ""DataExecucao"" DESC
                 LIMIT @limit";

    var rows = await conn.QueryAsync<BackupLog>(sql, prms);
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/resumo — último registro de cada banco (cards do dashboard)
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/resumo", async () =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var rows = await conn.QueryAsync<BackupLog>(@"
        SELECT DISTINCT ON (""BancoNome"") *
        FROM ""BackupLogs""
        ORDER BY ""BancoNome"", ""DataExecucao"" DESC");
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/tamanho?banco=food&dias=30 — série temporal para gráfico
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/tamanho", async (string? banco = null, int dias = 30, string? de = null, string? ate = null) =>
{
    await using var conn = new NpgsqlConnection(connStr);

    // Se de/ate informados, usa intervalo explícito; senão usa janela de N dias
    var usaIntervalo = !string.IsNullOrWhiteSpace(de) || !string.IsNullOrWhiteSpace(ate);
    DateTime dtDe  = usaIntervalo && !string.IsNullOrWhiteSpace(de)
                        ? DateTime.Parse(de)
                        : DateTime.UtcNow.AddDays(-dias);
    DateTime dtAte = usaIntervalo && !string.IsNullOrWhiteSpace(ate)
                        ? DateTime.Parse(ate).AddDays(1).AddSeconds(-1) // até 23:59:59 do dia
                        : DateTime.UtcNow;

    var rows = await conn.QueryAsync(@"
        SELECT
            DATE_TRUNC('hour', ""DataExecucao"") AS hora,
            ""BancoNome""                         AS banco,
            AVG(""TamanhoDadosGB"")               AS dados_gb,
            AVG(""TamanhoLogGB"")                 AS log_gb,
            AVG(""PercentualExpress"")             AS percentual_express,
            MAX(""StatusLimite"")                  AS status_limite
        FROM ""BackupLogs""
        WHERE ""TamanhoDadosGB"" > 0
          AND ""DataExecucao"" >= @dtDe
          AND ""DataExecucao"" <= @dtAte
          AND (@banco::TEXT IS NULL OR ""BancoNome"" = @banco)
        GROUP BY DATE_TRUNC('hour', ""DataExecucao""), ""BancoNome""
        ORDER BY hora ASC",
        new { banco, dtDe, dtAte });
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/bancos — lista de bancos únicos
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/bancos", async () =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var bancos = await conn.QueryAsync<string>(@"SELECT DISTINCT ""BancoNome"" FROM ""BackupLogs"" ORDER BY 1");
    return Results.Ok(bancos);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/alertas?limite=50 — últimos alertas e erros (todos os bancos)
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/alertas", async (int limite = 50) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var rows = await conn.QueryAsync<BackupLog>(@"
        SELECT * FROM ""BackupLogs""
        WHERE ""TipoBackup"" IN ('Erro', 'Alerta')
        ORDER BY ""DataExecucao"" DESC
        LIMIT @limite",
        new { limite = Math.Min(limite, 500) });
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/stats?banco=food — contadores OK/Erro/Alerta dos últimos 30 dias
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/stats", async (string? banco = null, int dias = 30) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var rows = await conn.QueryAsync(@"
        SELECT
            ""BancoNome""                                               AS banco,
            COUNT(*)                                                    AS total,
            COUNT(*) FILTER (WHERE ""TipoBackup"" = 'OK')              AS ok,
            COUNT(*) FILTER (WHERE ""TipoBackup"" = 'Erro')            AS erros,
            COUNT(*) FILTER (WHERE ""TipoBackup"" = 'Alerta')          AS alertas,
            COUNT(*) FILTER (WHERE ""TipoOperacao"" LIKE '%Shrink%')   AS shrinks,
            COUNT(*) FILTER (WHERE ""TipoOperacao"" = 'PressaoMemoria') AS pressao_mem,
            MAX(""TamanhoDadosGB"")                                     AS max_dados_gb,
            MAX(""TamanhoLogGB"")                                       AS max_log_gb,
            MAX(""DataExecucao"")                                       AS ultimo_evento
        FROM ""BackupLogs""
        WHERE ""DataExecucao"" >= NOW() - (@dias || ' days')::INTERVAL
          AND (@banco::TEXT IS NULL OR ""BancoNome"" = @banco)
        GROUP BY ""BancoNome""
        ORDER BY ultimo_evento DESC",
        new { banco, dias });
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/shrinks?banco=food&dias=90 — histórico de shrinks de log
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/shrinks", async (string? banco = null, int dias = 90) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var rows = await conn.QueryAsync<BackupLog>(@"
        SELECT * FROM ""BackupLogs""
        WHERE ""TipoOperacao"" IN ('ShrinkLog', 'ShrinkLogFalhou')
          AND ""DataExecucao"" >= NOW() - (@dias || ' days')::INTERVAL
          AND (@banco::TEXT IS NULL OR ""BancoNome"" = @banco)
        ORDER BY ""DataExecucao"" DESC",
        new { banco, dias });
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/memoria?banco=food&dias=30 — histórico de ajustes de memória e PLE
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/memoria", async (string? banco = null, int dias = 30) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var rows = await conn.QueryAsync<BackupLog>(@"
        SELECT * FROM ""BackupLogs""
        WHERE ""TipoOperacao"" IN ('AjusteMemoria', 'PressaoMemoria')
          AND ""DataExecucao"" >= NOW() - (@dias || ' days')::INTERVAL
          AND (@banco::TEXT IS NULL OR ""BancoNome"" = @banco)
        ORDER BY ""DataExecucao"" DESC",
        new { banco, dias });
    return Results.Ok(rows);
});

// Fallback → index.html (SPA)
app.MapFallbackToFile("index.html");

app.Run();

// ── DTO para /evento (MonitorClient) ─────────────────────────────────────────
record EventoMonitor(
    string?   Cliente,
    string?   Banco,
    string?   Estado,
    string?   Mensagem,
    string?   Ciclo,
    string?   Tamanho,
    int       IntervalHoras,
    DateTime? ProximaExecucao,
    string?   AlertaCiclo,
    string?   Estrategia,
    string?   TipoOperacao
);
