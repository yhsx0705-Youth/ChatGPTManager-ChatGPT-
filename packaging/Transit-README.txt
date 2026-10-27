此目录使用安装包附带的中转服务配置。首次使用时，在 ChatGPT Manager 的中转区域输入 API 密钥并保存；已有密钥可输入新密钥后点“替换密钥”。

密钥仅保存在本机 auth.json 中。若当前 ChatGPT 正在使用中转，保存时会同步替换当前配置中的 API 密钥；ChatGPT 运行时需先退出再保存。
config.toml 由软件提供。重新安装时，已有的 config.toml 和 auth.json 会保留，不会覆盖用户配置或密钥。
