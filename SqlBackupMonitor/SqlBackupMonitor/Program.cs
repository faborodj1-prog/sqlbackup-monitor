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
            ADD COLUMN IF NOT EXISTS ""IntervalHoras""    INTEGER       DEFAULT 0,
            ADD COLUMN IF NOT EXISTS ""ProximaExecucao""  TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS ""Estrategia""       TEXT          DEFAULT 'Simple',
            ADD COLUMN IF NOT EXISTS ""TipoOperacao""     TEXT          DEFAULT 'Diferencial',
            ADD COLUMN IF NOT EXISTS ""DiasFull""         TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""HoraFull""         TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""DiasIncremental""  TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""JanelaFullInicio"" TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""JanelaFullFim""    TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""DiaSemanaDbcc""    TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""HoraDbcc""         TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""DiaSemanaIndices"" TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""HoraIndices""      TEXT          DEFAULT '',
            ADD COLUMN IF NOT EXISTS ""EspacoLivreGB""    NUMERIC(10,3) DEFAULT 0,
            ADD COLUMN IF NOT EXISTS ""EspacoTotalGB""    NUMERIC(10,3) DEFAULT 0,
            ADD COLUMN IF NOT EXISTS ""DrivesJson""       TEXT          DEFAULT '';
    ");

    // ── Tabela de configuração remota por cliente+banco ──────────────────────
    // Migration segura: se existir tabela antiga (chave só BancoNome), recria.
    await conn.ExecuteAsync(@"
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_name = 'BackupConfigs'
            ) AND NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'BackupConfigs' AND column_name = 'ClienteCNPJ'
            ) THEN
                DROP TABLE ""BackupConfigs"";
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS ""BackupConfigs"" (
            ""ClienteCNPJ""  TEXT NOT NULL DEFAULT '',
            ""ClienteNome""  TEXT NOT NULL DEFAULT '',
            ""BancoNome""    TEXT NOT NULL,
            ""ConfigJson""   TEXT NOT NULL,
            ""UpdatedAt""    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            PRIMARY KEY (""ClienteCNPJ"", ""BancoNome"")
        );
        CREATE INDEX IF NOT EXISTS idx_bc_cnpj ON ""BackupConfigs""(""ClienteCNPJ"");
    ");

    // ── Tabela de configuração de app por cliente+banco ──────────────────────
    // Armazena enderecoBackup e qtdDiasApagarBkp lidos de TOTAL_CONFIG
    await conn.ExecuteAsync(@"
        CREATE TABLE IF NOT EXISTS ""AppConfigs"" (
            ""ClienteCNPJ""      TEXT NOT NULL DEFAULT '',
            ""ClienteNome""      TEXT NOT NULL DEFAULT '',
            ""BancoNome""        TEXT NOT NULL,
            ""EnderecoBackup""   TEXT NOT NULL DEFAULT '',
            ""QtdDiasApagarBkp"" INTEGER NOT NULL DEFAULT 30,
            ""UpdatedAt""        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            PRIMARY KEY (""ClienteCNPJ"", ""BancoNome"")
        );
        CREATE INDEX IF NOT EXISTS idx_ac_cnpj ON ""AppConfigs""(""ClienteCNPJ"");
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
            ""Estrategia"", ""TipoOperacao"",
            ""DiasFull"", ""HoraFull"", ""DiasIncremental"",
            ""JanelaFullInicio"", ""JanelaFullFim"",
            ""DiaSemanaDbcc"", ""HoraDbcc"",
            ""DiaSemanaIndices"", ""HoraIndices"",
            ""EspacoLivreGB"", ""EspacoTotalGB"",
            ""DrivesJson""
        ) VALUES (
            @DataExecucao, @ClienteNome, @ClienteCNPJ,
            @BancoNome, @TipoBackup, @Status, @NomeArquivo, @Ciclo,
            @Servidor, @Edicao, @Versao, @Recovery,
            @TamanhoDadosGB, @TamanhoLogGB, @PercentualExpress, @StatusLimite,
            @IntervalHoras, @ProximaExecucao,
            @Estrategia, @TipoOperacao,
            @DiasFull, @HoraFull, @DiasIncremental,
            @JanelaFullInicio, @JanelaFullFim,
            @DiaSemanaDbcc, @HoraDbcc,
            @DiaSemanaIndices, @HoraIndices,
            @EspacoLivreGB, @EspacoTotalGB,
            @DrivesJson
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
// GET /api/tamanho?cliente=08857449000182&banco=bo&dias=30&de=2026-01-01&ate=2026-03-31
// Quando `cliente` informado, filtra por CNPJ (normalizado) — ignora `banco`
// Quando `de`/`ate` informados, usa intervalo explícito; senão usa janela de `dias`
app.MapGet("/api/tamanho", async (string? banco = null, int dias = 30,
                                  string? de = null, string? ate = null,
                                  string? cliente = null) =>
{
    await using var conn = new NpgsqlConnection(connStr);

    var usaIntervalo = !string.IsNullOrWhiteSpace(de) || !string.IsNullOrWhiteSpace(ate);
    // DateTime.SpecifyKind(Unspecified) — Postgres usa timestamp without time zone
    DateTime dtDe  = DateTime.SpecifyKind(
        usaIntervalo && !string.IsNullOrWhiteSpace(de)
            ? DateTime.Parse(de)
            : DateTime.UtcNow.AddDays(-dias),
        DateTimeKind.Unspecified);
    DateTime dtAte = DateTime.SpecifyKind(
        usaIntervalo && !string.IsNullOrWhiteSpace(ate)
            ? DateTime.Parse(ate).AddDays(1).AddSeconds(-1)
            : DateTime.UtcNow,
        DateTimeKind.Unspecified);

    // Normaliza CNPJ para comparação (remove pontos, traços, barras e espaços)
    var cnpjNorm = string.IsNullOrWhiteSpace(cliente) ? null
        : new string(cliente.Where(char.IsDigit).ToArray());

    // Monta SQL dinamicamente — sem parâmetros NULL para evitar crash no Dapper+Postgres
    string sqlFiltro;
    object sqlParams;

    if (!string.IsNullOrWhiteSpace(cnpjNorm))
    {
        sqlFiltro  = @"AND REGEXP_REPLACE(""ClienteCNPJ"", '[^0-9]', '', 'g') = @cnpjNorm";
        sqlParams  = new { dtDe, dtAte, cnpjNorm };
    }
    else if (!string.IsNullOrWhiteSpace(banco))
    {
        sqlFiltro  = @"AND ""BancoNome"" = @banco";
        sqlParams  = new { dtDe, dtAte, banco };
    }
    else
    {
        sqlFiltro  = "";
        sqlParams  = new { dtDe, dtAte };
    }

    var sql = $@"
        SELECT
            DATE_TRUNC('hour', ""DataExecucao"") AS hora,
            ""ClienteNome""                       AS cliente,
            ""BancoNome""                         AS banco,
            AVG(""TamanhoDadosGB"")               AS dados_gb,
            AVG(""TamanhoLogGB"")                 AS log_gb,
            AVG(""PercentualExpress"")             AS percentual_express,
            MAX(""StatusLimite"")                  AS status_limite
        FROM ""BackupLogs""
        WHERE ""TamanhoDadosGB"" > 0
          AND ""DataExecucao"" >= @dtDe
          AND ""DataExecucao"" <= @dtAte
          {sqlFiltro}
        GROUP BY DATE_TRUNC('hour', ""DataExecucao""), ""ClienteNome"", ""BancoNome""
        ORDER BY hora ASC";

    var rows = await conn.QueryAsync(sql, sqlParams);
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

// ════════════════════════════════════════════════════════════════════════════
// GET /api/clientes — lista clientes distintos dos BackupLogs
// Retorna: cnpjNorm, cnpj, nome, ultimoEvento, bancosCount, temConfig
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/clientes", async () =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var rows = await conn.QueryAsync(@"
        SELECT
            cnpj_norm                                   AS ""cnpjNorm"",
            MAX(""ClienteCNPJ"")                        AS ""cnpj"",
            MAX(""ClienteNome"")                        AS ""nome"",
            MAX(""DataExecucao"")                       AS ""ultimoEvento"",
            COUNT(DISTINCT ""BancoNome"")               AS ""bancosCount"",
            EXISTS (
                SELECT 1 FROM ""BackupConfigs"" bc
                WHERE bc.""ClienteCNPJ"" = cnpj_norm
            )                                           AS ""temConfig""
        FROM (
            SELECT *,
                REGEXP_REPLACE(COALESCE(""ClienteCNPJ"", ''), '[^0-9]', '', 'g') AS cnpj_norm
            FROM ""BackupLogs""
            WHERE ""ClienteNome"" IS NOT NULL AND ""ClienteNome"" <> ''
        ) sub
        GROUP BY cnpj_norm
        ORDER BY MAX(""DataExecucao"") DESC");
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/config — lista clientes que possuem config remota
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/config", async () =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var rows = await conn.QueryAsync(@"
        SELECT ""ClienteCNPJ"" AS ""cnpjNorm"",
               MAX(""ClienteNome"") AS ""nome"",
               COUNT(*) AS ""bancosCount""
        FROM ""BackupConfigs""
        GROUP BY ""ClienteCNPJ""
        ORDER BY MAX(""ClienteNome"")");
    return Results.Ok(rows);
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/config/{cnpjNorm}/{banco} — retorna config de um banco (público)
// Usado pelo SqlBackup.exe (ConfigSync.cs) — autenticado por X-Api-Key
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/config/{cnpjNorm}/{banco}", async (string cnpjNorm, string banco, HttpContext ctx) =>
{
    // Aceita tanto requisições autenticadas (ConfigSync) quanto do painel admin
    if (ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) && key != apiKey)
        return Results.Unauthorized();

    await using var conn = new NpgsqlConnection(connStr);
    var row = await conn.QueryFirstOrDefaultAsync<(string ConfigJson, DateTime UpdatedAt)>(
        @"SELECT ""ConfigJson"", ""UpdatedAt"" FROM ""BackupConfigs""
          WHERE ""ClienteCNPJ"" = @cnpjNorm AND ""BancoNome"" = @banco",
        new { cnpjNorm, banco });

    if (row.ConfigJson == null) return Results.NotFound();

    // Achata: mescla updatedAt dentro do objeto de config
    var doc = System.Text.Json.JsonDocument.Parse(row.ConfigJson);
    using var ms  = new System.IO.MemoryStream();
    using var wrt = new System.Text.Json.Utf8JsonWriter(ms);
    wrt.WriteStartObject();
    foreach (var prop in doc.RootElement.EnumerateObject()) prop.WriteTo(wrt);
    wrt.WriteString("updatedAt", row.UpdatedAt);
    wrt.WriteEndObject();
    wrt.Flush();
    return Results.Content(System.Text.Encoding.UTF8.GetString(ms.ToArray()), "application/json");
});

// ════════════════════════════════════════════════════════════════════════════
// PUT /api/config/{cnpjNorm}/{banco} — salva ou atualiza config remota
// Payload: JSON com campos não-sensíveis + clienteNome opcional
// ════════════════════════════════════════════════════════════════════════════
app.MapPut("/api/config/{cnpjNorm}/{banco}", async (string cnpjNorm, string banco, HttpContext ctx) =>
{
    using var sr = new System.IO.StreamReader(ctx.Request.Body);
    var body     = await sr.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("Payload vazio.");

    // Extrai clienteNome do payload e reconstrói JSON sem ele
    string clienteNome = "";
    string configJson;
    try
    {
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(body);
        if (jsonDoc.RootElement.TryGetProperty("clienteNome", out var cn))
            clienteNome = cn.GetString() ?? "";

        using var ms  = new System.IO.MemoryStream();
        using var wrt = new System.Text.Json.Utf8JsonWriter(ms);
        wrt.WriteStartObject();
        foreach (var prop in jsonDoc.RootElement.EnumerateObject())
            if (prop.Name != "clienteNome") prop.WriteTo(wrt);
        wrt.WriteEndObject();
        wrt.Flush();
        configJson = System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
    catch { return Results.BadRequest("JSON inválido."); }

    await using var conn = new NpgsqlConnection(connStr);
    await conn.ExecuteAsync(@"
        INSERT INTO ""BackupConfigs"" (""ClienteCNPJ"", ""ClienteNome"", ""BancoNome"", ""ConfigJson"", ""UpdatedAt"")
        VALUES (@cnpjNorm, @clienteNome, @banco, @configJson, NOW())
        ON CONFLICT (""ClienteCNPJ"", ""BancoNome"") DO UPDATE
            SET ""ConfigJson""  = EXCLUDED.""ConfigJson"",
                ""ClienteNome"" = EXCLUDED.""ClienteNome"",
                ""UpdatedAt""   = NOW()",
        new { cnpjNorm, clienteNome, banco, configJson });

    return Results.Ok(new { message = $"Config de '{banco}' salva." });
});

// ════════════════════════════════════════════════════════════════════════════
// DELETE /api/config/{cnpjNorm}/{banco} — remove config remota
// ════════════════════════════════════════════════════════════════════════════
app.MapDelete("/api/config/{cnpjNorm}/{banco}", async (string cnpjNorm, string banco) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var affected = await conn.ExecuteAsync(
        @"DELETE FROM ""BackupConfigs"" WHERE ""ClienteCNPJ"" = @cnpjNorm AND ""BancoNome"" = @banco",
        new { cnpjNorm, banco });
    return affected > 0
        ? Results.Ok(new { message = "Config removida." })
        : Results.NotFound(new { message = "Config não encontrada." });
});

// ════════════════════════════════════════════════════════════════════════════
// GET /api/appconfig/{cnpjNorm}/{banco}
// Retorna { enderecoBackup, qtdDiasApagarBkp, updatedAt } ou 404.
// Autenticação aceita X-Api-Key (motor) ou sem auth (dashboard).
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/appconfig/{cnpjNorm}/{banco}", async (string cnpjNorm, string banco, HttpContext ctx) =>
{
    if (ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) && key != apiKey)
        return Results.Unauthorized();

    await using var conn = new NpgsqlConnection(connStr);
    var row = await conn.QueryFirstOrDefaultAsync(
        @"SELECT ""EnderecoBackup"", ""QtdDiasApagarBkp"", ""UpdatedAt""
          FROM ""AppConfigs""
          WHERE ""ClienteCNPJ"" = @cnpjNorm AND ""BancoNome"" = @banco",
        new { cnpjNorm, banco });

    if (row == null) return Results.NotFound();

    return Results.Ok(new
    {
        enderecoBackup    = (string)row.EnderecoBackup,
        qtdDiasApagarBkp  = (int)row.QtdDiasApagarBkp,
        updatedAt         = (DateTime)row.UpdatedAt
    });
});

// ════════════════════════════════════════════════════════════════════════════
// PUT /api/appconfig/{cnpjNorm}/{banco}
// Salva ou atualiza enderecoBackup e qtdDiasApagarBkp.
// Usado pelo motor (semeadura inicial) e pelo dashboard admin.
// ════════════════════════════════════════════════════════════════════════════
app.MapPut("/api/appconfig/{cnpjNorm}/{banco}", async (string cnpjNorm, string banco, HttpContext ctx) =>
{
    using var sr = new System.IO.StreamReader(ctx.Request.Body);
    var body     = await sr.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("Payload vazio.");

    string clienteNome    = "";
    string enderecoBackup = "";
    int    qtdDias        = 30;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root      = doc.RootElement;
        if (root.TryGetProperty("clienteNome",      out var cn)) clienteNome    = cn.GetString() ?? "";
        if (root.TryGetProperty("enderecoBackup",   out var eb)) enderecoBackup = eb.GetString() ?? "";
        if (root.TryGetProperty("qtdDiasApagarBkp", out var qd)) qtdDias        = qd.GetInt32();
    }
    catch { return Results.BadRequest("JSON inválido."); }

    await using var conn = new NpgsqlConnection(connStr);
    await conn.ExecuteAsync(@"
        INSERT INTO ""AppConfigs"" (""ClienteCNPJ"", ""ClienteNome"", ""BancoNome"", ""EnderecoBackup"", ""QtdDiasApagarBkp"", ""UpdatedAt"")
        VALUES (@cnpjNorm, @clienteNome, @banco, @enderecoBackup, @qtdDias, NOW())
        ON CONFLICT (""ClienteCNPJ"", ""BancoNome"") DO UPDATE
            SET ""EnderecoBackup""   = EXCLUDED.""EnderecoBackup"",
                ""QtdDiasApagarBkp"" = EXCLUDED.""QtdDiasApagarBkp"",
                ""ClienteNome""      = CASE WHEN EXCLUDED.""ClienteNome"" <> '' THEN EXCLUDED.""ClienteNome"" ELSE ""AppConfigs"".""ClienteNome"" END,
                ""UpdatedAt""        = NOW()",
        new { cnpjNorm, clienteNome, banco, enderecoBackup, qtdDias });

    return Results.Ok(new { message = $"App Config de '{banco}' salva." });
});

// ════════════════════════════════════════════════════════════════════════════
// DELETE /api/appconfig/{cnpjNorm}/{banco} — remove app config
// ════════════════════════════════════════════════════════════════════════════
app.MapDelete("/api/appconfig/{cnpjNorm}/{banco}", async (string cnpjNorm, string banco) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    var affected = await conn.ExecuteAsync(
        @"DELETE FROM ""AppConfigs"" WHERE ""ClienteCNPJ"" = @cnpjNorm AND ""BancoNome"" = @banco",
        new { cnpjNorm, banco });
    return affected > 0
        ? Results.Ok(new { message = "App Config removida." })
        : Results.NotFound(new { message = "App Config não encontrada." });
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
