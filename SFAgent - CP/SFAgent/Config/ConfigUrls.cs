namespace SFAgent.Config
{
    public static class ConfigUrls
    {
        public static string InstanceBase =
            "https://acos-continente--homolog.sandbox.my.salesforce.com";

        public static string ApiVersion = "v60.0";

        public static string AuthUrl =
            $"{InstanceBase}/services/oauth2/token";

        public static string ApiRoot =
            $"{InstanceBase}/services/data/{ApiVersion}";

        public static string ApiQueryBase =
            $"{ApiRoot}/query";

        public static string ApiCondicaoBase =
            $"{ApiRoot}/sobjects/CA_CondicaoPagamento__c";

        // Campo External ID configurado no objeto
        public static string ApiCondicaoExternalField = "CA_IdExterno__c";
    }
}
