using System.Collections.Concurrent;
using System.Runtime.Caching;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyKit;

namespace Onllama.ModelScope2Registry
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var digestDict = new ConcurrentDictionary<string, string>();
            var redirectDict = new ConcurrentDictionary<string, string>();
            var lenDict = new ConcurrentDictionary<string, long>();

            var templateMapDict = new ConcurrentDictionary<string, string>();
            var templateStrDict = new ConcurrentDictionary<string, string>();
            var paramsStrDict = new ConcurrentDictionary<string, string>();

            var modelConfig = new HttpClient()
                .GetStringAsync("https://raw.githubusercontent.com/onllama/templates/refs/heads/main/config.json").Result;

            Parallel.ForEach(JsonNode.Parse(new HttpClient()
                    .GetStringAsync("https://fastly.jsdelivr.net/gh/ollama/ollama/template/index.json").Result)
                ?.AsArray() ?? [], i =>
            {
                try
                {
                    var name = i?["name"]?.ToString() ?? string.Empty;
                    templateMapDict.TryAdd(i["template"]?.ToString().Trim(), name);
                    templateStrDict.TryAdd(name, new HttpClient()
                        .GetStringAsync($"https://fastly.jsdelivr.net/gh/ollama/ollama/template/{name}.gotmpl").Result);
                    paramsStrDict.TryAdd(name, new HttpClient()
                        .GetStringAsync($"https://fastly.jsdelivr.net/gh/ollama/ollama/template/{name}.json").Result);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            });

            Parallel.ForEach(JsonNode.Parse(new HttpClient()
                    .GetStringAsync("https://fastly.jsdelivr.net/gh/onllama/templates/index.json").Result)
                ?.AsArray() ?? [], i =>
            {
                try
                {
                    var name = i?["name"]?.ToString() ?? string.Empty;
                    templateMapDict.TryAdd(i["template"]?.ToString().Trim(), name);
                    templateStrDict.TryAdd(name, new HttpClient()
                        .GetStringAsync($"https://fastly.jsdelivr.net/gh/onllama/templates/{name}.gotmpl").Result);
                    paramsStrDict.TryAdd(name, new HttpClient()
                        .GetStringAsync($"https://fastly.jsdelivr.net/gh/onllama/templates/{name}.json").Result);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            });

            // Add services to the container.
            builder.Services.AddAuthorization();
            builder.Services.AddProxy(httpClientBuilder =>
                httpClientBuilder.ConfigureHttpClient(client =>
                    client.Timeout = TimeSpan.FromMinutes(5)));

            var app = builder.Build();

            app.UseHttpsRedirection();
            app.UseAuthorization();

            app.Use(async (context, next) =>
            {
                Console.WriteLine(context.Request.Path);
                await next.Invoke();
            });

            app.Map("/v2/{user}/{repo}/blobs/{digest}", async context =>
            {
                var digest = context.Request.RouteValues["digest"].ToString();

                if (digestDict.TryGetValue(digest, out var value))
                {
                    context.Response.Headers.Location = context.Request.Path.Value;
                    context.Response.Headers.ContentLength = Encoding.UTF8.GetByteCount(value);
                    context.Response.Headers.TryAdd("Content-Type", "application/octet-stream");
                    if (context.Request.Method.ToUpper() != "HEAD") await context.Response.WriteAsync(value);
                }
                else if (redirectDict.TryGetValue(digest, out var url))
                {
                    try
                    {
                        if (context.Request.Method.ToUpper() == "HEAD")
                            context.Response.Headers.ContentLength = lenDict[digest];
                        else
                        {
                            #region ForwardProxy

                            //context.Response.Headers.TryAdd("X-Forwarder-By", "ModelScope2Registry");
                            //context.Response.Headers.Location = context.Request.Path.Value;

                            //var reqContext = context.DeepClone();
                            //reqContext.Request.Path = new Uri(url).AbsolutePath;
                            //var response = await reqContext.ForwardTo(new Uri("https://www.modelscope.cn/")).Send();
                            //var reStream = context.Response.BodyWriter.AsStream();
                            //await (await response.Content.ReadAsStreamAsync()).CopyToAsync(reStream);

                            #endregion

                            context.Response.Redirect(url);
                            context.Response.StatusCode = 307;
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
                else
                {
                    await context.Response.WriteAsJsonAsync(new
                        { errors = new { code = "BLOB_UNKNOWN", message = "Blob Unknown" } });
                    context.Response.StatusCode = 404;
                }
            });

            app.Map("/v2/{user}/{repo}/manifests/{tag}", async context =>
            {
                try
                {
                    var user = context.Request.RouteValues["user"].ToString();
                    var repo = context.Request.RouteValues["repo"].ToString();
                    var tag = context.Request.RouteValues["tag"].ToString();

                    var templateTag = string.Empty;
                    if (tag != null && tag.Contains("--"))
                    {
                        var sp = tag.Split("--");
                        tag = sp.First();
                        templateTag = sp.Last();
                    }

                    var modelScope = JsonSerializer.Deserialize<ModelScope>(
                        await GetWithCache($"https://www.modelscope.cn/api/v1/models/{user}/{repo}/repo/files"));
                    var files = modelScope.Data.Files.OrderBy(x => x.Size)
                        .Where(x => x.Name.EndsWith(".gguf") && !x.Name.Contains("-of-"));
                    var gguf = tag != "latest"
                        ? files.FirstOrDefault(x =>
                            x.Name.Split("-").Last().Split('.').First().ToUpper() == tag.ToUpper())
                        : files.FirstOrDefault(x =>
                              x.Name.Split("-").Last().Split('.').First().ToUpper() is "Q4_K_M" or "Q8_0") ??
                          files.FirstOrDefault(x =>
                              x.Name.Split("-").Last().Split('.').First().ToUpper() is "Q4_0");

                    if (gguf == null)
                    {
                        await context.Response.WriteAsJsonAsync(new
                            {errors = new {code = "MANIFEST_UNKNOWN", message = "Manifest Unknown"}});
                        context.Response.StatusCode = 404;
                        return;
                    }

                    var ggufDigest = $"sha256:{gguf.Sha256}";
                    redirectDict.TryAdd(ggufDigest,
                        $"https://www.modelscope.cn/models/{user}/{repo}/resolve/master/{gguf.Name}");
                    lenDict.TryAdd(ggufDigest, gguf.Size);

                    object config;
                    var layers = new List<object>
                    {
                        new
                        {
                            mediaType = "application/vnd.ollama.image.model",
                            size = gguf.Size,
                            digest = ggufDigest
                        }
                    };

                    try
                    {
                        var metadata = JsonNode.Parse(JsonNode.Parse(await PostWithCache(
                                "https://www.modelscope.cn/api/v1/rm/fc?Type=model_view",
                                JsonSerializer.Serialize(new
                                    {modelPath = user, modelName = repo, filePath = gguf.Name})))?
                            ["Data"]?["metadata"]?.ToString() ?? string.Empty);

                        if (metadata != null)
                        {
                            var templateName = string.Empty;
                            var configStr = modelConfig.Replace("<@MODEL>",
                                    metadata["general.architecture"]?.ToString() ?? string.Empty)
                                .Replace("<@SIZE>", metadata["general.size_label"]?.ToString() ?? string.Empty)
                                .Replace("<@QUANT>", gguf.Name.Split("-").Last().Split('.').First().ToUpper());
                            var configByte = Encoding.UTF8.GetBytes(configStr);
                            var configDigest =
                                $"sha256:{BitConverter.ToString(SHA256.HashData(configByte)).Replace("-", string.Empty).ToLower()}";
                            digestDict.TryAdd(configDigest, configStr);

                            config = new
                            {
                                mediaType = "application/vnd.docker.container.image.v1+json",
                                size = configByte.Length,
                                digest = configDigest
                            };

                            if (metadata["tokenizer.chat_template"] != null && templateMapDict.TryGetValue(
                                    metadata["tokenizer.chat_template"]?.ToString().Trim() ?? string.Empty,
                                    out templateName) || templateStrDict.ContainsKey(templateTag))
                            {
                                templateName ??= templateTag;
                                if (templateStrDict.TryGetValue(templateName, out var templateStr))
                                {
                                    var templateByte = Encoding.UTF8.GetBytes(templateStr);
                                    var templateDigest =
                                        $"sha256:{BitConverter.ToString(SHA256.HashData(templateByte)).Replace("-", string.Empty).ToLower()}";
                                    digestDict.TryAdd(templateDigest, templateStr);
                                    layers.Add(new
                                    {
                                        mediaType = "application/vnd.ollama.image.template",
                                        size = templateByte.Length,
                                        digest = templateDigest
                                    });
                                }

                                if (paramsStrDict.TryGetValue(templateName, out var paramsStr))
                                {
                                    var paramsByte = Encoding.UTF8.GetBytes(paramsStr);
                                    var paramsDigest =
                                        $"sha256:{BitConverter.ToString(SHA256.HashData(paramsByte)).Replace("-", string.Empty).ToLower()}";
                                    digestDict.TryAdd(paramsDigest, paramsStr);
                                    layers.Add(new
                                    {
                                        mediaType = "application/vnd.ollama.image.params",
                                        size = paramsByte.Length,
                                        digest = paramsDigest
                                    });
                                }
                            }
                            else throw new Exception("Template Not Found");
                        }
                        else throw new Exception("Metadata Not Found");
                    }
                    catch (Exception e)
                    {
                        var configStr = modelConfig.Replace("<@MODEL>", "unknown")
                            .Replace("<@SIZE>", "unknown")
                            .Replace("<@QUANT>", gguf.Name.Split("-").Last().Split('.').First().ToUpper());
                        var configByte = Encoding.UTF8.GetBytes(configStr);
                        var configDigest =
                            $"sha256:{BitConverter.ToString(SHA256.HashData(configByte)).Replace("-", string.Empty).ToLower()}";
                        digestDict.TryAdd(configDigest, configStr);

                        config = new
                        {
                            mediaType = "application/vnd.docker.container.image.v1+json",
                            size = configByte.Length,
                            digest = configDigest
                        };
                        Console.WriteLine(e);
                    }

                    await context.Response.WriteAsJsonAsync(new
                    {
                        schemaVersion = 2,
                        mediaType = "application/vnd.docker.distribution.manifest.v2+json",
                        config,
                        layers
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    await context.Response.WriteAsJsonAsync(new
                        {errors = new {code = "MANIFEST_UNKNOWN", message = e.Message}});
                    context.Response.StatusCode = 404;
                }
            });

            app.Run();
        }

        public static async Task<string> GetWithCache(string key, int minutes = 15)
        {
            if (MemoryCache.Default.Contains("GET:" + key))
                return MemoryCache.Default.Get("GET:" + key).ToString() ??
                       await new HttpClient().GetStringAsync(key);

            var stringAsync = await new HttpClient().GetStringAsync(key);
            MemoryCache.Default.Add("GET:" + key, stringAsync, DateTimeOffset.Now.AddMinutes(minutes));
            return stringAsync;
        }

        public static async Task<string> PostWithCache(string key, string body, int minutes = 15)
        {
            if (MemoryCache.Default.Contains("POST:" + key + ":" + body))
                return MemoryCache.Default.Get("POST:" + key + ":" + body).ToString() ??
                       await (await new HttpClient().PostAsync(key, new StringContent(body))).Content
                           .ReadAsStringAsync();

            var stringAsync = await (await new HttpClient().PostAsync(key, new StringContent(body))).Content
                .ReadAsStringAsync();
            MemoryCache.Default.Add("POST:" + key + ":" + body, stringAsync, DateTimeOffset.Now.AddMinutes(minutes));
            return stringAsync;
        }
    }

    public class Data
    {
        public List<File> Files { get; set; }
        public int IsVisual { get; set; }
        public LatestCommitter LatestCommitter { get; set; }
    }

    public class File
    {
        public string CommitMessage { get; set; }
        public long CommittedDate { get; set; }
        public string CommitterName { get; set; }
        public bool InCheck { get; set; }
        public bool IsLFS { get; set; }
        public string Mode { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Revision { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Type { get; set; }
    }

    public class LatestCommitter
    {
        public string AuthorEmail { get; set; }
        public string AuthorName { get; set; }
        public long AuthoredDate { get; set; }
        public long CommittedDate { get; set; }
        public string CommitterEmail { get; set; }
        public string CommitterName { get; set; }
        public long CreatedAt { get; set; }
        public string Id { get; set; }
        public string Message { get; set; }
        public List<string> ParentIds { get; set; }
        public string ShortId { get; set; }
        public string Title { get; set; }
    }

    public class ModelScope
    {
        public int Code { get; set; }
        public Data Data { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public bool Success { get; set; }
    }
}
