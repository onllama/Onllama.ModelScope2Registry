## Onllama.ModelScope2Registry 
ModelScope2Registry 是 Ollama 到 ModelScope 的模型 Registry 镜像站 / 加速器，它为 ModelScope 补齐更多了 Ollama Registry Manifests 信息，使 Ollama 能够从 ModelScope 魔搭 更快的 拉取 / 下载 模型。 
## 快速开始
### 拉取模型
请选择带有 GGUF 模型的仓库：
```
ollama run modelscope2ollama-registry.azurewebsites.net/qwen/Qwen2.5-7B-Instruct-gguf
```
这将能够拉取 `https://www.modelscope.cn/models/qwen/Qwen2.5-0.5B-Instruct-gguf` 中的模型，对于不带有标签或 latest 将依次按顺序尝试选择`Q4_K_M`、`Q4_0`、`Q8_0`量化。
### 指定量化
可以通过 tag 指定选择的量化：
```
ollama run modelscope2ollama-registry.azurewebsites.net/qwen/Qwen2.5-7B-Instruct-gguf:Q8_0
```
这将能够拉取 `https://www.modelscope.cn/models/qwen/Qwen2.5-7B-Instruct-gguf/resolve/master/qwen2.5-0.5b-instruct-q8_0.gguf` ，量化类型标签不区分大小写，你可以在 [这里](https://github.com/ollama/ollama/blob/main/docs/import.md#supported-quantizations) 查看 Ollama 支持的量化。

仓库中需要包含带有正确格式文件名的 GGUF 文件（模型名称以“-”分隔，最后一位需要为有效的量化类型，形如：model-quant.gguf），暂不支持包含类似 `0000x-of-0000x` 的切分后的模型。
### 指定模板
若对话模板未能正确识别或识别有误导致对话输出异常，你可以尝试这样指定模型的对话模板：
```
ollama run modelscope2ollama-registry.azurewebsites.net/qwen/Qwen2.5-7B-Instruct-gguf:Q8_0--qwen2
```
你可以查看 [Ollama 官方支持的模板](https://github.com/ollama/ollama/tree/main/template)，和 [我们支持的模板](https://github.com/onllama/templates)，以手动指定更加合适的模板。
