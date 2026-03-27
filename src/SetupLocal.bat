dotnet user-secrets set -p .\Agency.Sql.Postgre.Test "ConnectionStrings:PostgreSql" "super-secret-value" 


dotnet user-secrets set -p .\Agency.Llm.Test "LlmTest:OpenAI:ApiKey" "super-secret-value"


dotnet user-secrets set -p .\Agency.Llm.Test "LlmTest:Claude:ApiKey" "super-secret-value"

