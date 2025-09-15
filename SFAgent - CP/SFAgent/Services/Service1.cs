using System;
using System.Globalization;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using SFAgent.Salesforce;
using SFAgent.Sap;
using SFAgent.Utils;

namespace SFAgent.Services
{
    public partial class Service1 : ServiceBase
    {
        private SalesforceAuth _auth;
        private SalesforceApi _api;
        private Timer _timer;

        public Service1()
        {
            InitializeComponent();
            _auth = new SalesforceAuth();
            _api = new SalesforceApi();
        }

        protected override void OnStart(string[] args)
        {
            Logger.InitLog();

            if (!System.Diagnostics.EventLog.SourceExists("SFAgent"))
                System.Diagnostics.EventLog.CreateEventSource("SFAgent", "Application");

            Task.Run(async () =>
            {
                try
                {
                    await SincronizarCondicoesPagamento(); // <<-- roda o SYNC
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro inicial no OnStart: {ex.Message}");
                }
            });

            _timer = new Timer(async _ =>
            {
                try
                {
                    await SincronizarCondicoesPagamento(); // <<-- roda o SYNC a cada ciclo
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro no Timer: {ex.Message}");
                }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        protected override void OnStop()
        {
            _timer?.Dispose();
            Logger.Log("Serviço parado.");
        }

        // ---------- Helpers ----------
        private static bool IsDbNull(object v) => v == null || v == DBNull.Value;
        private static string S(object v) => IsDbNull(v) ? null : v.ToString();
        private static string DateYMD(object v)
        {
            if (IsDbNull(v)) return null;
            if (v is DateTime dt) return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Convert.ToDateTime(v).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        private static string ToSN(object v)
        {
            var s = v?.ToString()?.Trim().ToUpperInvariant();
            return (s == "Y" || s == "S" || s == "SIM" || s == "1" || s == "TRUE") ? "S" : "N";
        }
        // --------- /Helpers ----------

        private async Task SincronizarCondicoesPagamento()
        {
            try
            {
                var token = await _auth.GetValidToken();
                var sap = new SapConnector("HANADB:30015", "SBO_ACOS_TESTE", "B1ADMIN", "S4P@2Q60_tm2");

                // 1) TUDO da SF (ext -> Id)
                var sfMap = await _api.GetAllCondicaoPagamentoIdsByExternal(token);

                // 2) TUDO do SAP (GroupNum como externalId)
                var sql = @"
                    SELECT
                        ""GroupNum"", ""PymntGroup"", ""DataSource"", ""PaymntsNum"", ""CrdMthd"", ""UpdateDate"",
                        ""U_CodAcoflex"", ""U_Parcelas"", ""U_SX_Sifra"", ""U_SX_Adiantamento"", ""U_AC_PrazoMedio"", ""OpenRcpt""
                    FROM ""OCTG""
                ";
                var sapRows = sap.ExecuteQuery(sql);

                int insertCount = 0;
                int updateCount = 0;
                int errorCount = 0;

                var sapExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in sapRows)
                {
                    var idExterno = S(r["GroupNum"]);
                    if (!string.IsNullOrWhiteSpace(idExterno))
                        sapExts.Add(idExterno);
                }

                // 3) DELETE na SF do que não existe no SAP
                var toDelete = sfMap.Keys.Where(ext => !sapExts.Contains(ext)).ToList();
                foreach (var ext in toDelete)
                {
                    try
                    {
                        var id = sfMap[ext];
                        await _api.DeleteCondicaoPagamentoById(token, id);
                        Logger.Log($"DELETE SF CondiçãoPgto OK | ExternalId={ext} | SFID={id}");
                    }
                    catch (Exception delEx)
                    {
                        Logger.Log($"DELETE SF CondiçãoPgto FALHOU | ExternalId={ext} | Erro={delEx.Message}", asError: true);
                    }
                }

                // 4) UPSERT de tudo que veio do SAP
                foreach (var cond in sapRows)
                {
                    var idExterno = S(cond["GroupNum"]);
                    if (string.IsNullOrWhiteSpace(idExterno))
                    {
                        Logger.Log("OCTG ignorado: GroupNum vazio.");
                        continue;
                    }

                    SalesforceApi.UpsertResult up = null;

                    try
                    {
                        var body = new
                        {
                            Name = S(cond["PymntGroup"]),
                            CA_CodCondPagamento__c = S(cond["GroupNum"]),
                            CA_NumeroGrupo__c = S(cond["GroupNum"]),
                            CA_FonteDados__c = S(cond["DataSource"]) ?? "I",
                            CA_NumPrestacoes__c = S(cond["PaymntsNum"]) ?? "0",
                            CA_MetodoCredito__c = S(cond["CrdMthd"]) ?? "E",
                            CA_DataAtualizacao__c = DateYMD(cond["UpdateDate"]),
                            CA_CondAcoflex__c = S(cond["U_CodAcoflex"]) ?? "N",
                            CA_QuantParcelas__c = S(cond["U_Parcelas"]) ?? "0",
                            CA_CondSifra__c = S(cond["U_SX_Sifra"]) ?? "N",
                            CA_GeraAtendimento__c = ToSN(cond["U_SX_Adiantamento"]),
                            CA_PrazoMedioCond__c = S(cond["U_AC_PrazoMedio"]) ?? "0",
                            CA_Ativo__c = string.Equals(S(cond["OpenRcpt"]), "Y", StringComparison.OrdinalIgnoreCase)
                        };

                        up = await _api.UpsertCondicaoPagamento(token, idExterno, body);
                        Logger.Log($"METHOD={up.Method} SF CondiçãoPgto {up.Outcome} | ExternalId={idExterno} | HTTP={up.StatusCode}");
                    }
                    catch (Exception upEx)
                    {
                        errorCount++;

                        var rowJson = JsonConvert.SerializeObject(cond);
                        Logger.Log($"ERRO METHOD={up?.Method ?? "N/A"} SF CondiçãoPgto | ExternalId={idExterno} | Erro={upEx.Message} | Row={rowJson}", asError: true);
                    }
                }

                Logger.Log($"Sync CondiçõesPgto finalizado. | Inseridos={insertCount} | Atualizados={updateCount} | Removidos={toDelete.Count} | Erros={errorCount} | Total SAP={sapExts.Count}.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro geral na sincronização de condições de pagamento: {ex.Message}", asError: true);
            }
        }
    }
}
