# SqlBackup Monitor — API + Painel Web

## O que é

API C# (.NET 8) que recebe eventos do SqlBackup.exe e exibe um painel web com:
- Cards de status atual por banco
- Gráfico de tamanho ao longo do tempo
- Tabela de histórico completo com filtros

---

## Deploy no Render (passo a passo)

### 1. Suba o código para um repositório GitHub
- Crie um repositório no GitHub (pode ser privado)
- Suba esta pasta `SqlBackupMonitor/` na raiz

### 2. Crie o Web Service no Render
1. Acesse https://render.com → **New → Web Service**
2. Conecte seu repositório GitHub
3. O Render detecta o `render.yaml` automaticamente
4. Clique em **Create Web Service**

### 3. Configure as variáveis de ambiente
No painel do Render → seu serviço → **Environment**:

| Variável        | Valor                                              |
|-----------------|----------------------------------------------------|
| `DATABASE_URL`  | Cole a **Internal Database URL** do seu PostgreSQL |
| `API_KEY`       | Gerado automaticamente (copie para o SqlBackup)    |

> A Internal Database URL está em: Render → seu banco PostgreSQL → **Connect → Internal Database URL**
> Formato: `postgresql://user:senha@host/dbname`

### 4. Aguarde o deploy
O primeiro deploy leva ~2 minutos. Quando ficar verde, acesse a URL do serviço — o painel já estará disponível.

---

## Configurar o SqlBackup.exe para enviar para a API

No `appsettings.json` do SqlBackup (ou pela tela de configuração):

```json
{
  "Backup": {
    "TelegramAtivo": true,
    "TelegramToken":  "SEU_TOKEN",
    "TelegramChatId": "SEU_CHAT_ID",

    "MonitorAtivo":   true,
    "MonitorUrl":     "https://SEU-APP.onrender.com",
    "MonitorApiKey":  "VALOR_DA_API_KEY_DO_RENDER"
  }
}
```

---

## Endpoints da API

| Método | Endpoint           | Descrição                          |
|--------|--------------------|------------------------------------|
| POST   | `/evento`          | Recebe evento do SqlBackup.exe     |
| GET    | `/api/resumo`      | Último estado de cada banco        |
| GET    | `/api/historico`   | Histórico com filtros              |
| GET    | `/api/tamanho`     | Série temporal para o gráfico      |
| GET    | `/api/bancos`      | Lista de bancos cadastrados        |

### Autenticação
Todos os POSTs precisam do header:
```
X-Api-Key: SUA_API_KEY
```

### Payload do POST /evento
```json
{
  "ClienteNome":   "Restaurante Exemplo Ltda",
  "ClienteDoc":    "CNPJ: 12.345.678/0001-99",
  "Banco":         "food",
  "Ciclo":         "01032026",
  "Estado":        "OK",
  "Mensagem":      "Incremental concluído — food",
  "Servidor":      "SERVIDOR01\\SQLEXPRESS",
  "EdicaoSql":     "Express Edition",
  "VersaoSql":     "16.0.4165.4",
  "RecoveryModel": "SIMPLE",
  "DadosGb":       3.412,
  "LogGb":         0.128,
  "PercentualExpr": 34.12,
  "StatusLimite":  "OK (Dentro do limite Express)"
}
```
