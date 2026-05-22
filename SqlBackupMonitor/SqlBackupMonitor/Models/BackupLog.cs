namespace BackupMonitorApi.Models;

public class BackupLog
{
    public int      Id                { get; set; }   // PK — gerado pelo banco
    public DateTime DataExecucao      { get; set; }
    public string   ClienteNome       { get; set; } = "";
    public string   ClienteCNPJ       { get; set; } = "";
    public string   BancoNome         { get; set; } = "";
    public string   TipoBackup        { get; set; } = "";  // OK | Erro | Alerta
    public string   Status            { get; set; } = "";  // mensagem descritiva
    public string   NomeArquivo       { get; set; } = "";
    public string   Ciclo             { get; set; } = "";
    public string   Servidor          { get; set; } = "";
    public string   Edicao            { get; set; } = "";
    public string   Versao            { get; set; } = "";
    public string   Recovery          { get; set; } = "";
    public decimal  TamanhoDadosGB    { get; set; }
    public decimal  TamanhoLogGB      { get; set; }
    public decimal  PercentualExpress { get; set; }
    public string   StatusLimite      { get; set; } = "";
    public int      IntervalHoras     { get; set; }
    public DateTime? ProximaExecucao  { get; set; }

    // ── Campos novos (estratégia adaptativa) ────────────────────────
    // Simple | Full
    public string Estrategia   { get; set; } = "Simple";
    // Diferencial | Log | Full | Diferencial (âncora) |
    // ShrinkLog | ShrinkLogFalhou | AjusteMemoria | PressaoMemoria |
    // MudancaParaSimple | MudancaParaFull | FalhaFullPreSimple
    public string TipoOperacao { get; set; } = "Diferencial";

    // ── Configuração de agendamento ──────────────────────────────────
    public string DiasFull         { get; set; } = "";  // "Segunda,Quarta,Sexta"
    public string HoraFull         { get; set; } = "";  // "01:00"
    public string DiasIncremental  { get; set; } = "";  // "Terça,Quinta" (ou vazio = todos os dias)
    public string JanelaFullInicio { get; set; } = "";  // "00:30"
    public string JanelaFullFim    { get; set; } = "";  // "03:00"
    public string DiaSemanaDbcc    { get; set; } = "";  // "Sábado"
    public string HoraDbcc         { get; set; } = "";  // "23:00"
    public string DiaSemanaIndices { get; set; } = "";  // "Domingo"
    public string HoraIndices      { get; set; } = "";  // "02:00"

    // ── Espaço em disco (drive da PastaBase) ─────────────────────────
    public decimal EspacoLivreGB { get; set; }
    public decimal EspacoTotalGB { get; set; }

    // ── Todos os drives fixos do servidor ─────────────────────────────
    // JSON compacto: [{"l":"C:","livre":50.1,"total":200.0}, ...]
    public string DrivesJson { get; set; } = "";
}
