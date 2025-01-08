using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Onllama.ModelScope2Registry
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var DigestDict = new Dictionary<string, string>();
            var RedirectDict = new Dictionary<string, string>();

            var modelConfig =
                """
                {"model_format":"gguf","model_family":"<@MODEL>","model_families":["<@MODEL>"],"model_type":"<@SIZE>","file_type":"unknown","architecture":"amd64","os":"linux","rootfs":{"type":"layers","diff_ids":[]}}
                """;

            // Add services to the container.
            builder.Services.AddAuthorization();

            var app = builder.Build();

            app.UseHttpsRedirection();
            app.UseAuthorization();

            app.Use(async (context, next) =>
            {
                Console.WriteLine(context.Request.Path);
                await next.Invoke();
            });

            app.Map("/www.modelscope.cn/{**path}", async context =>
            {
                var path = context.Request.RouteValues["path"].ToString();
                context.Response.Redirect("https://www.modelscope.cn/" + path, false);
                await context.Response.WriteAsJsonAsync(new { path });
            });

            app.Map("/v2/{user}/{repo}/blobs/{digest}", async context =>
            {
                var digest = context.Request.RouteValues["digest"].ToString();

                if (DigestDict.TryGetValue(digest, out var value))
                {
                    await context.Response.WriteAsync(value);
                }
                else if (RedirectDict.TryGetValue(digest, out var url))
                {
                    context.Response.Redirect(url + "?hash=" + digest.Split(":").Last(), false);
                    context.Response.Headers.AcceptRanges = "bytes";
                    context.Response.StatusCode = 307;
                    await context.Response.WriteAsJsonAsync(new { url });
                }
                else
                {
                    await context.Response.WriteAsJsonAsync(new {error = "Blob not found"});
                    context.Response.StatusCode = 404;
                }
            });

            app.Map("/v2/{user}/{repo}/manifests/{tag}", async context =>
            {
                var user = context.Request.RouteValues["user"].ToString();
                var repo = context.Request.RouteValues["repo"].ToString();
                var tag = context.Request.RouteValues["tag"].ToString();

                var fileRes = await new HttpClient().GetStringAsync(
                    $"https://www.modelscope.cn/api/v1/models/{user}/{repo}/repo/files");
                var modelScope = JsonSerializer.Deserialize<ModelScope>(fileRes);
                var ggufFiles = modelScope.Data.Files.Where(x => x.Name.EndsWith(".gguf") && !x.Name.Contains("-of-"));
                var ggufFile = ggufFiles.FirstOrDefault(x => x.Name.Split("-").Last().Split('.').First().ToUpper() == tag.ToUpper());
                if (ggufFile == null)
                {
                    await context.Response.WriteAsJsonAsync(new { error = "Model not found" });
                    context.Response.StatusCode = 404;
                    return;
                }

                var ggufDigest = $"sha256:{ggufFile.Sha256}";
                RedirectDict.TryAdd(ggufDigest, $"https://www.modelscope.cn/models/{user}/{repo}/resolve/master/{ggufFile.Name}");
                var layers = new List<object>
                {
                    new
                    {
                        mediaType = "application/vnd.ollama.image.model",
                        size = ggufFile.Size,
                        digest = ggufDigest
                    }
                };
                var config = new object();

                try
                {
                    var ggufRes = await (await new HttpClient().PostAsync("https://www.modelscope.cn/api/v1/rm/fc?Type=model_view",
                        new StringContent(JsonSerializer.Serialize(new { modelPath = user, modelName = repo, filePath = ggufFile.Name })))).Content.ReadAsStringAsync();
                    var metadata = JsonNode.Parse(JsonNode.Parse(ggufRes)?["Data"]?["metadata"]?.ToString() ?? string.Empty);

                    if (metadata != null)
                    {
                        var configStr = modelConfig.Replace("<@MODEL>", metadata["general.architecture"]?.ToString() ?? string.Empty)
                            .Replace("<@SIZE>", metadata["general.size_label"]?.ToString() ?? string.Empty);
                        var configByte = Encoding.UTF8.GetBytes(configStr);
                        var configDigest =
                            $"sha256:{BitConverter.ToString(SHA256.HashData(configByte)).Replace("-", string.Empty).ToLower()}";
                        var configSize = configByte.Length;
                        DigestDict.TryAdd(configDigest, configStr);

                        config = new
                        {
                            mediaType = "application/vnd.docker.container.image.v1+json",
                            size = configSize,
                            digest = configDigest
                        };


                        //if (metadata["tokenizer.chat_template"] != null)
                        //{
                        //    var templateStr = metadata["tokenizer.chat_template"]?.ToString() ?? string.Empty;
                        //    var templateByte = Encoding.UTF8.GetBytes(templateStr);
                        //    var templateDigest = $"sha256:{BitConverter.ToString(SHA256.HashData(templateByte)).Replace("-", string.Empty).ToLower()}";
                        //    var templateSize = templateByte.Length;
                        //    DigestDict.TryAdd(templateDigest, templateStr);
                        //    layers.Add(new
                        //    {
                        //        mediaType = "application/vnd.ollama.image.template",
                        //        size = templateSize,
                        //        digest = templateDigest
                        //    });
                        //}
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                await context.Response.WriteAsJsonAsync(new
                {
                    schemaVersion = 2,
                    mediaType = "application/vnd.docker.distribution.manifest.v2+json",
                    config,
                    layers
                });
            });

            app.Run();
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
        public int CommittedDate { get; set; }
        public string CommitterName { get; set; }
        public bool InCheck { get; set; }
        public bool IsLFS { get; set; }
        public string Mode { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Revision { get; set; }
        public string Sha256 { get; set; }
        public int Size { get; set; }
        public string Type { get; set; }
    }

    public class LatestCommitter
    {
        public string AuthorEmail { get; set; }
        public string AuthorName { get; set; }
        public int AuthoredDate { get; set; }
        public int CommittedDate { get; set; }
        public string CommitterEmail { get; set; }
        public string CommitterName { get; set; }
        public int CreatedAt { get; set; }
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
