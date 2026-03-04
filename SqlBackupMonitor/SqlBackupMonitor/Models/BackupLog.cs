namespace BackupMonitorApi.Models;

public class BackupLog
{
    public int      Id                { get; set; }  // PK — gerado pelo banco
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
    public int       IntervalHoras     { get; set; }         // intervalo do ciclo em horas
    public DateTime? ProximaExecucao   { get; set; }         // Atualizado + IntervalHoras (nullable — campo novo)
}
