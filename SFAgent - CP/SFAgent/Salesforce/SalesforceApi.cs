using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SFAgent.Config;

namespace SFAgent.Salesforce
{
    public class SalesforceApi
    {
        private static readonly HttpClient _http = new HttpClient();

        public class UpsertResult
        {
            public string Method { get; set; } = "PATCH";
            public string Outcome { get; set; } // INSERT | UPDATE | SUCCESS
            public int StatusCode { get; set; }
            public string SalesforceId { get; set; }
            public string RawBody { get; set; }
        }

        internal class SalesforceUpsertResponse
        {
            public string id { get; set; }
            public bool success { get; set; }
            public bool created { get; set; }
            public object[] errors { get; set; }
        }

        // --- QUERY: pega todos os (Id, ExternalId) já existentes na SF ---
        public async Task<Dictionary<string, string>> GetAllCondicaoPagamentoIdsByExternal(string token)
        {
            var soql = "SELECT Id, CA_IdExterno__c FROM CA_CondicaoPagamento__c WHERE CA_IdExterno__c != null";
            var url = $"{ConfigUrls.ApiQueryBase}?q={Uri.EscapeDataString(soql)}";

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrEmpty(url))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Erro no QUERY Condição Pagamento (HTTP {(int)resp.StatusCode}): {body}");

                var jo = JObject.Parse(body);
                foreach (var rec in (JArray)jo["records"])
                {
                    var id = (string)rec["Id"];
                    var ext = (string)rec["CA_IdExterno__c"];
                    if (!string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(id))
                        map[ext] = id;
                }

                // paginação
                var next = (string)jo["nextRecordsUrl"];
                url = string.IsNullOrEmpty(next) ? null : (ConfigUrls.InstanceBase.TrimEnd('/') + next);
            }

            return map;
        }

        // --- DELETE por Id ---
        public async Task<bool> DeleteCondicaoPagamentoById(string token, string salesforceId)
        {
            var url = $"{ConfigUrls.ApiCondicaoBase}/{salesforceId}";
            var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return true;

            var body = await resp.Content.ReadAsStringAsync();
            throw new Exception($"Erro ao DELETAR Condição Pagamento Id={salesforceId} (HTTP {(int)resp.StatusCode}): {body}");
        }

        // --- UPSERT por ExternalId (PATCH) ---
        public async Task<UpsertResult> UpsertCondicaoPagamento(string token, string idExterno, object condicao)
        {
            var externalPath = $"{ConfigUrls.ApiCondicaoBase}/{ConfigUrls.ApiCondicaoExternalField}/{Uri.EscapeDataString(idExterno)}";
            var json = JsonConvert.SerializeObject(condicao);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var req = new HttpRequestMessage(new HttpMethod("PATCH"), externalPath);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = content;

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            var result = new UpsertResult
            {
                Method = "PATCH",
                StatusCode = (int)resp.StatusCode,
                RawBody = body
            };

            if (resp.IsSuccessStatusCode)
            {
                if (result.StatusCode == 201)
                {
                    result.Outcome = "POST";
                    try
                    {
                        var parsed = JsonConvert.DeserializeObject<SalesforceUpsertResponse>(body);
                        result.SalesforceId = parsed?.id;
                    }
                    catch { }
                }
                else if (result.StatusCode == 204)
                {
                    result.Outcome = "PATCH";
                }
                else
                {
                    result.Outcome = "SUCCESS";
                }
                return result;
            }

            throw new Exception($"Erro no UPSERT Condição Pagamento (HTTP {result.StatusCode}): {body}");
        }
    }
}
