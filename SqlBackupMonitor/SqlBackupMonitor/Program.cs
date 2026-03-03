using BackupMonitorApi.Models;
using Dapper;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.UseStaticFiles(); // serve wwwroot/index.html

// ── Configuração ──────────────────────────────────────────────────────────────
// Render: configure as env vars DATABASE_URL e API_KEY no painel Environment
var connStr = Environment.GetEnvironmentVariable("DATABASE_URL")
           ?? app.Configuration.GetConnectionString("DefaultConnection")
           ?? throw new Exception("DATABASE_URL não configurada.");

var apiKey = Environment.GetEnvironmentVariable("API_KEY") ?? "dev-key-local";

// ── Migração automática ───────────────────────────────────────────────────────
await using (var conn = new NpgsqlConnection(connStr))
{
    await conn.OpenAsync();
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
            ""StatusLimite""      TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_bl_banco   ON ""BackupLogs""(""BancoNome"");
        CREATE INDEX IF NOT EXISTS idx_bl_data    ON ""BackupLogs""(""DataExecucao"" DESC);
        CREATE INDEX IF NOT EXISTS idx_bl_tipo    ON ""BackupLogs""(""TipoBackup"");
    ");
}

// ════════════════════════════════════════════════════════════════════════════
// POST /api/backup — recebe evento do SqlBackup.exe
// Header: X-Api-Key: <chave configurada no Render>
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
            ""TamanhoDadosGB"", ""TamanhoLogGB"", ""PercentualExpress"", ""StatusLimite""
        ) VALUES (
            @DataExecucao, @ClienteNome, @ClienteCNPJ,
            @BancoNome, @TipoBackup, @Status, @NomeArquivo, @Ciclo,
            @Servidor, @Edicao, @Versao, @Recovery,
            @TamanhoDadosGB, @TamanhoLogGB, @PercentualExpress, @StatusLimite
        )", log);

    return Results.Ok(new { message = "Backup registrado com sucesso" });
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/historico?limit=100&banco=food&tipo=Erro
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/historico", async (int limit = 100, string? banco = null, string? tipo = null) =>
{
    await using var conn = new NpgsqlConnection(connStr);

    var where = new List<string>();
    var prms  = new DynamicParameters();

    if (!string.IsNullOrWhiteSpace(banco)) { where.Add(@"""BancoNome"" = @banco"); prms.Add("banco", banco); }
    if (!string.IsNullOrWhiteSpace(tipo))  { where.Add(@"""TipoBackup"" = @tipo");  prms.Add("tipo",  tipo);  }
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
app.MapGet("/api/tamanho", async (string? banco = null, int dias = 30) =>
{
    await using var conn = new NpgsqlConnection(connStr);
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
          AND ""DataExecucao"" >= NOW() - (@dias || ' days')::INTERVAL
          AND (@banco::TEXT IS NULL OR ""BancoNome"" = @banco)
        GROUP BY DATE_TRUNC('hour', ""DataExecucao""), ""BancoNome""
        ORDER BY hora ASC",
        new { banco, dias });
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/bancos — lista de bancos para popular os selects do painel
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/bancos", async () =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var bancos = await conn.QueryAsync<string>(@"SELECT DISTINCT ""BancoNome"" FROM ""BackupLogs"" ORDER BY 1");
    return Results.Ok(bancos);
});

// Fallback → index.html (SPA)
app.MapFallbackToFile("index.html");

app.Run();
