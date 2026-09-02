using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CharacterFactory.Core
{
    [Serializable, JsonConverter(typeof(ApiWarningConverter))]
    public class ApiWarning
    {
        [JsonProperty("code")] public string Code;
        [JsonProperty("message")] public string Message;
        public override string ToString() => string.IsNullOrEmpty(Code) ? Message : $"{Code}: {Message}";
    }

    /// <summary>
    /// Current servers return structured warning objects. Accept the OpenAPI-documented string
    /// shape as well so a contract correction or older local service produces a useful warning
    /// instead of breaking the entire character record.
    /// </summary>
    public sealed class ApiWarningConverter : JsonConverter
    {
        public override bool CanWrite => true;
        public override bool CanConvert(Type objectType) => objectType == typeof(ApiWarning);

        public override object ReadJson(
            JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            if (reader.TokenType == JsonToken.String)
                return new ApiWarning { Message = (string)reader.Value };
            var value = JObject.Load(reader);
            return new ApiWarning
            {
                Code = value.Value<string>("code"),
                Message = value.Value<string>("message") ?? value.Value<string>("error"),
            };
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var warning = (ApiWarning)value;
            if (warning == null) { writer.WriteNull(); return; }
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(warning.Code))
            {
                writer.WritePropertyName("code");
                writer.WriteValue(warning.Code);
            }
            writer.WritePropertyName("message");
            writer.WriteValue(warning.Message);
            writer.WriteEndObject();
        }
    }

    [Serializable]
    public class ApiError
    {
        [JsonProperty("error")] public string Error;
        [JsonProperty("code")] public string Code;
        [JsonProperty("retryable")] public bool Retryable;
    }

    [Serializable]
    public class JobError
    {
        [JsonProperty("code")] public string Code;
        [JsonProperty("message")] public string Message;
        [JsonProperty("retryable")] public bool Retryable;
    }

    [Serializable]
    public class CharacterJobResult
    {
        [JsonProperty("character_id")] public string CharacterId;
        [JsonProperty("revision")] public int Revision;
    }

    [Serializable]
    public class CharacterJob
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("operation")] public string Operation;
        [JsonProperty("status")] public string Status;
        [JsonProperty("stage")] public string Stage;
        [JsonProperty("progress")] public float Progress;
        [JsonProperty("detail")] public string Detail;
        [JsonProperty("queue_position")] public int? QueuePosition;
        [JsonProperty("requested_interpreter")] public string RequestedInterpreter;
        [JsonProperty("actual_interpreter")] public string ActualInterpreter;
        [JsonProperty("warnings")] public List<ApiWarning> Warnings = new List<ApiWarning>();
        [JsonProperty("result")] public CharacterJobResult Result;
        [JsonProperty("error")] public JobError Error;
        [JsonProperty("created_at")] public string CreatedAt;
        [JsonProperty("updated_at")] public string UpdatedAt;
        [JsonProperty("stage_started_at")] public string StageStartedAt;
        [JsonProperty("last_heartbeat")] public string LastHeartbeat;
        [JsonProperty("finished_at")] public string FinishedAt;

        public bool IsTerminal => Status == "succeeded" || Status == "failed" || Status == "cancelled";
        public bool IsSucceeded => Status == "succeeded";
        public bool IsFailed => Status == "failed";
        public bool IsCancelled => Status == "cancelled";
    }

    [Serializable]
    public class CharacterArtifact
    {
        [JsonProperty("available")] public bool Available;
        [JsonProperty("revision")] public int Revision;
        [JsonProperty("bytes")] public long? Bytes;
        [JsonProperty("sha256")] public string Sha256;
        [JsonProperty("built_at")] public string BuiltAt;
    }

    [Serializable]
    public class CharacterCreation
    {
        [JsonProperty("requested_interpreter")] public string RequestedInterpreter;
        [JsonProperty("actual_interpreter")] public string ActualInterpreter;
        [JsonProperty("warnings")] public List<ApiWarning> Warnings = new List<ApiWarning>();
    }

    /// <summary>A completed-library row, or the detailed response from GET /v0/characters/{id}.</summary>
    [Serializable]
    public class CharacterRecord
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("artifact")] public CharacterArtifact Artifact;
        [JsonProperty("latest_job")] public CharacterJob LatestJob;
        [JsonProperty("creation")] public CharacterCreation Creation;
        [JsonProperty("created_at")] public string CreatedAt;
        [JsonProperty("updated_at")] public string UpdatedAt;
        public bool IsAvailable => Artifact != null && Artifact.Available;
    }

    [Serializable]
    public class CreateCharacterRequest
    {
        [JsonProperty("prompt")] public string Prompt;
        [JsonProperty("interpreter", NullValueHandling = NullValueHandling.Ignore)] public string Interpreter;
        [JsonProperty("turbo")] public bool Turbo;
        [JsonProperty("seed", NullValueHandling = NullValueHandling.Ignore)] public long? Seed;
        [JsonIgnore] public string IdempotencyKey = CharacterFactoryClient.NewIdempotencyKey();
    }

    [Serializable]
    public class RebuildCharacterRequest
    {
        [JsonProperty("from")] public string From = "assemble";
        [JsonProperty("turbo")] public bool Turbo;
        [JsonIgnore] public string IdempotencyKey = CharacterFactoryClient.NewIdempotencyKey();
    }

    public sealed class CharacterFactoryApiException : HttpRequestException
    {
        public int StatusCode { get; }
        public string Code { get; }
        public bool Retryable { get; }
        public string ResponseBody { get; }

        public CharacterFactoryApiException(int statusCode, ApiError error, string responseBody, string route)
            : base($"{route} failed ({statusCode}): {error?.Error ?? Truncate(responseBody)}")
        {
            StatusCode = statusCode;
            Code = error?.Code;
            Retryable = error?.Retryable ?? false;
            ResponseBody = responseBody;
        }

        static string Truncate(string value) => string.IsNullOrEmpty(value)
            ? "no response body"
            : value.Length > 300 ? value.Substring(0, 300) + "…" : value;
    }

    /// <summary>
    /// Async HTTP client for the character-factory v0 API. Character creation and rebuilds are
    /// durable jobs; completed artifacts are separate character records.
    /// </summary>
    public sealed class CharacterFactoryClient
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        public string BaseUrl { get; }

        public CharacterFactoryClient(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Server address is empty. Configure it (see ServerAddress) or pass --server.");
            BaseUrl = baseUrl.TrimEnd('/');
        }

        public static string NewIdempotencyKey() => Guid.NewGuid().ToString("N");

        public async Task<CharacterRecord[]> ListCharactersAsync(CancellationToken ct = default)
        {
            var json = await GetStringAsync("/v0/characters", ct);
            return JsonConvert.DeserializeObject<CharacterRecord[]>(json) ?? Array.Empty<CharacterRecord>();
        }

        public async Task<CharacterRecord> GetCharacterAsync(string id, CancellationToken ct = default)
        {
            RequireId(id, nameof(id));
            var json = await GetStringAsync($"/v0/characters/{id}", ct);
            return JsonConvert.DeserializeObject<CharacterRecord>(json);
        }

        public Task<CharacterJob> CreateCharacterAsync(
            string prompt, string interpreter = null, bool turbo = false,
            long? seed = null, string idempotencyKey = null, CancellationToken ct = default)
        {
            return CreateCharacterAsync(new CreateCharacterRequest
            {
                Prompt = prompt,
                Interpreter = interpreter,
                Turbo = turbo,
                Seed = seed,
                IdempotencyKey = idempotencyKey ?? NewIdempotencyKey(),
            }, ct);
        }

        public async Task<CharacterJob> CreateCharacterAsync(CreateCharacterRequest request, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is empty.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) request.IdempotencyKey = NewIdempotencyKey();
            var json = await SendJsonAsync(HttpMethod.Post, "/v0/characters", request, request.IdempotencyKey, ct);
            return RequireJob(JsonConvert.DeserializeObject<CharacterJob>(json), "POST /v0/characters");
        }

        public async Task<CharacterJob> RebuildCharacterAsync(
            string characterId, RebuildCharacterRequest request = null, CancellationToken ct = default)
        {
            RequireId(characterId, nameof(characterId));
            request ??= new RebuildCharacterRequest();
            if (request.From != "assemble" && request.From != "bake")
                throw new ArgumentException("Rebuild 'from' must be 'assemble' or 'bake'.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) request.IdempotencyKey = NewIdempotencyKey();
            var route = $"/v0/characters/{characterId}/rebuild";
            var json = await SendJsonAsync(HttpMethod.Post, route, request, request.IdempotencyKey, ct);
            return RequireJob(JsonConvert.DeserializeObject<CharacterJob>(json), $"POST {route}");
        }

        public async Task<CharacterJob> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            RequireId(jobId, nameof(jobId));
            var json = await GetStringAsync($"/v0/jobs/{jobId}", ct);
            return RequireJob(JsonConvert.DeserializeObject<CharacterJob>(json), $"GET /v0/jobs/{jobId}");
        }

        public async Task<CharacterJob> CancelJobAsync(string jobId, CancellationToken ct = default)
        {
            RequireId(jobId, nameof(jobId));
            var route = $"/v0/jobs/{jobId}";
            var json = await SendAsync(HttpMethod.Delete, route, null, null, ct);
            return RequireJob(JsonConvert.DeserializeObject<CharacterJob>(json), $"DELETE {route}");
        }

        public async Task<CharacterJob> RetryJobAsync(string jobId, CancellationToken ct = default)
        {
            RequireId(jobId, nameof(jobId));
            var route = $"/v0/jobs/{jobId}/retry";
            var json = await SendJsonAsync(HttpMethod.Post, route, new { }, null, ct);
            return RequireJob(JsonConvert.DeserializeObject<CharacterJob>(json), $"POST {route}");
        }

        public async Task<CharacterJob> WaitForJobAsync(
            string jobId, TimeSpan timeout, Action<CharacterJob> onProgress = null,
            TimeSpan? pollInterval = null, CancellationToken ct = default)
        {
            RequireId(jobId, nameof(jobId));
            var deadline = DateTime.UtcNow + timeout;
            var delay = pollInterval ?? TimeSpan.FromSeconds(2);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var job = await GetJobAsync(jobId, ct);
                onProgress?.Invoke(job);
                if (job.IsSucceeded)
                {
                    if (job.Result == null || string.IsNullOrEmpty(job.Result.CharacterId))
                        throw new InvalidOperationException($"Job {job.Id} succeeded without a character result.");
                    return job;
                }
                if (job.IsFailed)
                    throw new InvalidOperationException(
                        $"Character Factory job {job.Id} failed at '{job.Stage}': " +
                        (job.Error?.Message ?? job.Detail ?? "no error detail"));
                if (job.IsCancelled)
                    throw new InvalidOperationException($"Character Factory job {job.Id} was cancelled.");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        $"Job {job.Id} did not finish after {timeout.TotalSeconds:F0}s " +
                        $"(last status: {job.Status}, stage: {job.Stage}, progress: {job.Progress:P0}).");
                await Task.Delay(delay, ct);
            }
        }

        public async Task DownloadSceneGlbAsync(string id, string destinationPath, CancellationToken ct = default)
        {
            RequireId(id, nameof(id));
            var bytes = await GetBytesAsync($"/v0/characters/{id}/scene.glb", ct);
            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(destinationPath, bytes);
        }

        public async Task<string> GetCharacterDocumentAsync(string id, CancellationToken ct = default)
        {
            RequireId(id, nameof(id));
            return await GetStringAsync($"/v0/characters/{id}/character.json", ct);
        }

        public async Task<ExportManifest> GetManifestAsync(string id, CancellationToken ct = default)
        {
            RequireId(id, nameof(id));
            var json = await GetStringAsync($"/v0/characters/{id}/manifest.json", ct);
            return ExportManifest.FromJson(Newtonsoft.Json.Linq.JObject.Parse(json));
        }

        async Task<string> GetStringAsync(string route, CancellationToken ct)
            => await SendAsync(HttpMethod.Get, route, null, null, ct);

        async Task<byte[]> GetBytesAsync(string route, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + route);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw ToApiException((int)response.StatusCode, body, $"GET {route}");
            }
            return await response.Content.ReadAsByteArrayAsync();
        }

        async Task<string> SendJsonAsync(HttpMethod method, string route, object payload, string idempotencyKey, CancellationToken ct)
            => await SendAsync(method, route, JsonConvert.SerializeObject(payload), idempotencyKey, ct);

        async Task<string> SendAsync(
            HttpMethod method, string route, string jsonBody, string idempotencyKey, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, BaseUrl + route);
            if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            using var response = await Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw ToApiException((int)response.StatusCode, body, $"{method} {route}");
            return body;
        }

        static CharacterFactoryApiException ToApiException(int status, string body, string route)
        {
            ApiError error = null;
            try { error = JsonConvert.DeserializeObject<ApiError>(body); }
            catch (JsonException) { }
            return new CharacterFactoryApiException(status, error, body, route);
        }

        static CharacterJob RequireJob(CharacterJob job, string operation)
        {
            if (job == null || string.IsNullOrEmpty(job.Id) || string.IsNullOrEmpty(job.Status))
                throw new InvalidDataException($"{operation} returned an invalid job document.");
            return job;
        }

        static void RequireId(string id, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Resource id is empty.", parameterName);
        }
    }

    public static class ServerAddress
    {
        public const string EnvVar = "CHARACTER_FACTORY_URL";
        public const string DefaultAddress = "http://localhost:8400";

        public static string Resolve(string explicitValue = null, string storedValue = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitValue)) return explicitValue.Trim();
            var env = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            if (!string.IsNullOrWhiteSpace(storedValue)) return storedValue.Trim();
            return DefaultAddress;
        }
    }
}
