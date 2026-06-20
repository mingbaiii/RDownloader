use std::path::PathBuf;

/// 命令枚举
#[derive(Debug, Clone)]
pub enum Command {
    /// 下载文件
    Download {
        url: String,
        output: Option<String>,
    },
    /// 列出所有下载
    List,
    /// 获取下载详情
    Info {
        id: String,
    },
    /// 获取所有下载详情（包含 segments）
    InfoAll {
        id: String,
    },
    /// 监控下载
    Monitor {
        id: String,
    },
    /// 暂停下载
    Pause {
        id: String,
    },
    /// 恢复下载
    Resume {
        id: String,
    },
    /// 取消下载
    Cancel {
        id: String,
    },
    /// 添加 endpoint
    AddEndpoint {
        id: String,
        ip: String,
        proxy: Option<String>,
    },
    /// 禁用 endpoint
    DisableEndpoint {
        id: String,
        ip: String,
    },
    /// 启用 endpoint
    EnableEndpoint {
        id: String,
        ip: String,
    },
    /// 设置配置
    SetConfig {
        key: String,
        value: String,
    },
    /// 执行下载循环
    Loop,
    /// 显示帮助
    Help,
    /// 退出
    Exit,
}

/// CLI 配置
#[derive(Debug, Clone)]
pub struct CliConfig {
    /// 是否为 JSON 模式
    pub json_mode: bool,
    /// 配置文件路径
    pub config_path: Option<PathBuf>,
}

impl Default for CliConfig {
    fn default() -> Self {
        Self {
            json_mode: false,
            config_path: None,
        }
    }
}

/// 解析命令行参数
pub fn parse_args() -> (CliConfig, Option<Command>) {
    let args: Vec<String> = std::env::args().collect();
    let mut config = CliConfig::default();
    let mut command = None;
    let mut i = 1;

    while i < args.len() {
        match args[i].as_str() {
            "--json" | "-j" => {
                config.json_mode = true;
                i += 1;
            }
            "--config" => {
                if i + 1 < args.len() {
                    config.config_path = Some(PathBuf::from(&args[i + 1]));
                    i += 2;
                } else {
                    eprintln!("Error: --config requires a path");
                    std::process::exit(1);
                }
            }
            "download" | "dl" => {
                let url = if i + 1 < args.len() {
                    args[i + 1].clone()
                } else {
                    eprintln!("Error: download requires a URL");
                    std::process::exit(1);
                };
                let mut output = None;
                let mut j = i + 2;
                while j < args.len() {
                    if (args[j] == "--output" || args[j] == "-o") && j + 1 < args.len() {
                        output = Some(args[j + 1].clone());
                        j += 2;
                    } else {
                        j += 1;
                    }
                }
                command = Some(Command::Download { url, output });
                i = j;
            }
            "list" | "ls" => {
                command = Some(Command::List);
                i += 1;
            }
            "info" | "i" => {
                if i + 1 < args.len() {
                    if args[i + 1] == "all" {
                        // info all - 显示所有下载详情（包含 segments）
                        if i + 2 < args.len() {
                            command = Some(Command::InfoAll { id: args[i + 2].clone() });
                            i += 3;
                        } else {
                            eprintln!("Error: info all requires an ID");
                            std::process::exit(1);
                        }
                    } else {
                        command = Some(Command::Info { id: args[i + 1].clone() });
                        i += 2;
                    }
                } else {
                    eprintln!("Error: info requires an ID");
                    std::process::exit(1);
                }
            }
            "monitor" | "m" => {
                if i + 1 < args.len() {
                    command = Some(Command::Monitor { id: args[i + 1].clone() });
                    i += 2;
                } else {
                    eprintln!("Error: monitor requires an ID");
                    std::process::exit(1);
                }
            }
            "pause" => {
                if i + 1 < args.len() {
                    command = Some(Command::Pause { id: args[i + 1].clone() });
                    i += 2;
                } else {
                    eprintln!("Error: pause requires an ID");
                    std::process::exit(1);
                }
            }
            "resume" => {
                if i + 1 < args.len() {
                    command = Some(Command::Resume { id: args[i + 1].clone() });
                    i += 2;
                } else {
                    eprintln!("Error: resume requires an ID");
                    std::process::exit(1);
                }
            }
            "cancel" => {
                if i + 1 < args.len() {
                    command = Some(Command::Cancel { id: args[i + 1].clone() });
                    i += 2;
                } else {
                    eprintln!("Error: cancel requires an ID");
                    std::process::exit(1);
                }
            }
            "add-endpoint" => {
                if i + 2 < args.len() {
                    let id = args[i + 1].clone();
                    let ip = args[i + 2].clone();
                    let mut proxy = None;
                    let mut j = i + 3;
                    while j < args.len() {
                        if args[j] == "--proxy" && j + 1 < args.len() {
                            proxy = Some(args[j + 1].clone());
                            j += 2;
                        } else {
                            j += 1;
                        }
                    }
                    command = Some(Command::AddEndpoint { id, ip, proxy });
                    i = j;
                } else {
                    eprintln!("Error: add-endpoint requires ID and IP");
                    std::process::exit(1);
                }
            }
            "disable-endpoint" => {
                if i + 2 < args.len() {
                    command = Some(Command::DisableEndpoint {
                        id: args[i + 1].clone(),
                        ip: args[i + 2].clone(),
                    });
                    i += 3;
                } else {
                    eprintln!("Error: disable-endpoint requires ID and IP");
                    std::process::exit(1);
                }
            }
            "enable-endpoint" => {
                if i + 2 < args.len() {
                    command = Some(Command::EnableEndpoint {
                        id: args[i + 1].clone(),
                        ip: args[i + 2].clone(),
                    });
                    i += 3;
                } else {
                    eprintln!("Error: enable-endpoint requires ID and IP");
                    std::process::exit(1);
                }
            }
            "set-config" => {
                if i + 2 < args.len() {
                    command = Some(Command::SetConfig {
                        key: args[i + 1].clone(),
                        value: args[i + 2].clone(),
                    });
                    i += 3;
                } else {
                    eprintln!("Error: set-config requires key and value");
                    std::process::exit(1);
                }
            }
            "loop" => {
                command = Some(Command::Loop);
                i += 1;
            }
            "help" | "-h" | "--help" => {
                command = Some(Command::Help);
                i += 1;
            }
            "exit" | "quit" | "q" => {
                command = Some(Command::Exit);
                i += 1;
            }
            _ => {
                eprintln!("Error: Unknown command '{}'", args[i]);
                std::process::exit(1);
            }
        }
    }

    (config, command)
}

/// 智能分割参数，支持引号包围的字符串和花括号包围的 JSON
fn split_args(input: &str) -> Vec<String> {
    let mut args = Vec::new();
    let mut current = String::new();
    let mut in_quotes = false;
    let mut quote_char = '"';
    let mut brace_count = 0;  // 花括号计数
    let mut chars = input.chars().peekable();

    while let Some(c) = chars.next() {
        match c {
            '"' | '\'' => {
                if in_quotes {
                    if c == quote_char {
                        // 结束引号
                        in_quotes = false;
                    } else {
                        current.push(c);
                    }
                } else if brace_count > 0 {
                    // 在花括号内的引号，保持原样
                    current.push(c);
                } else {
                    // 开始引号
                    in_quotes = true;
                    quote_char = c;
                }
            }
            '{' => {
                current.push(c);
                if !in_quotes {
                    brace_count += 1;
                }
            }
            '}' => {
                current.push(c);
                if !in_quotes && brace_count > 0 {
                    brace_count -= 1;
                    // 如果花括号闭合，结束当前参数
                    if brace_count == 0 && !current.is_empty() {
                        args.push(current.clone());
                        current.clear();
                    }
                }
            }
            ' ' | '\t' => {
                if in_quotes || brace_count > 0 {
                    current.push(c);
                } else if !current.is_empty() {
                    args.push(current.clone());
                    current.clear();
                }
            }
            _ => {
                current.push(c);
            }
        }
    }

    if !current.is_empty() {
        args.push(current);
    }

    args
}

/// 解析交互式输入的命令
pub fn parse_interactive_command(input: &str) -> Result<Command, String> {
    let input = input.trim();
    if input.is_empty() {
        return Err("Empty command".to_string());
    }

    // 智能分割：支持引号包围的参数
    let parts = split_args(input);
    if parts.is_empty() {
        return Err("Empty command".to_string());
    }

    match parts[0].as_str() {
        "download" | "dl" => {
            if parts.len() < 2 {
                return Err("Usage: download <url> [--output <path>]".to_string());
            }
            let url = parts[1].clone();
            let mut output = None;
            let mut i = 2;
            while i < parts.len() {
                if (parts[i] == "--output" || parts[i] == "-o") && i + 1 < parts.len() {
                    output = Some(parts[i + 1].clone());
                    i += 2;
                } else {
                    i += 1;
                }
            }
            Ok(Command::Download { url, output })
        }
        "list" | "ls" => Ok(Command::List),
        "info" | "i" => {
            if parts.len() < 2 {
                return Err("Usage: info <id> or info all <id>".to_string());
            }
            if parts[1] == "all" {
                if parts.len() < 3 {
                    return Err("Usage: info all <id>".to_string());
                }
                Ok(Command::InfoAll { id: parts[2].to_string() })
            } else {
                Ok(Command::Info { id: parts[1].to_string() })
            }
        }
        "monitor" | "m" => {
            if parts.len() < 2 {
                return Err("Usage: monitor <id>".to_string());
            }
            Ok(Command::Monitor { id: parts[1].to_string() })
        }
        "pause" => {
            if parts.len() < 2 {
                return Err("Usage: pause <id>".to_string());
            }
            Ok(Command::Pause { id: parts[1].clone() })
        }
        "resume" => {
            if parts.len() < 2 {
                return Err("Usage: resume <id>".to_string());
            }
            Ok(Command::Resume { id: parts[1].clone() })
        }
        "cancel" => {
            if parts.len() < 2 {
                return Err("Usage: cancel <id>".to_string());
            }
            Ok(Command::Cancel { id: parts[1].clone() })
        }
        "add-endpoint" => {
            if parts.len() < 3 {
                return Err("Usage: add-endpoint <id> <ip> [--proxy <proxy>]".to_string());
            }
            let id = parts[1].clone();
            let ip = parts[2].clone();
            let mut proxy = None;
            let mut i = 3;
            while i < parts.len() {
                if parts[i] == "--proxy" && i + 1 < parts.len() {
                    proxy = Some(parts[i + 1].clone());
                    i += 2;
                } else {
                    i += 1;
                }
            }
            Ok(Command::AddEndpoint { id, ip, proxy })
        }
        "disable-endpoint" => {
            if parts.len() < 3 {
                return Err("Usage: disable-endpoint <id> <ip>".to_string());
            }
            Ok(Command::DisableEndpoint {
                id: parts[1].clone(),
                ip: parts[2].clone(),
            })
        }
        "enable-endpoint" => {
            if parts.len() < 3 {
                return Err("Usage: enable-endpoint <id> <ip>".to_string());
            }
            Ok(Command::EnableEndpoint {
                id: parts[1].clone(),
                ip: parts[2].clone(),
            })
        }
        "set-config" => {
            if parts.len() < 3 {
                return Err("Usage: set-config <key> <value>".to_string());
            }
            // 对于 set-config，value 是第三个参数之后的所有内容
            let value = if parts.len() > 3 {
                parts[2..].join(" ")
            } else {
                parts[2].clone()
            };
            Ok(Command::SetConfig {
                key: parts[1].clone(),
                value,
            })
        }
        "help" | "h" => Ok(Command::Help),
        "exit" | "quit" | "q" => Ok(Command::Exit),
        _ => Err(format!("Unknown command: {}", parts[0])),
    }
}

/// 生成 8 位短 UUID（只使用 a-f 字符）
pub fn generate_id() -> String {
    use rand::Rng;
    let mut rng = rand::thread_rng();
    let chars: Vec<char> = "abcdef".chars().collect();
    (0..8).map(|_| chars[rng.gen_range(0..chars.len())]).collect()
}

/// 显示帮助信息
pub fn show_help(config_path: Option<&str>) {
    println!("Commands:");
    println!("  download <url> [--output <path>]    Start a download");
    println!("  list                                List all downloads");
    println!("  info <id>                           Show download details");
    println!("  info all <id>                       Show download details with segments");
    println!("  monitor <id>                        Monitor download with TUI");
    println!("  pause <id>                          Pause download");
    println!("  resume <id>                         Resume download");
    println!("  cancel <id>                         Cancel download");
    println!("  add-endpoint <id> <ip> [--proxy <p>] Add endpoint");
    println!("  disable-endpoint <id> <ip>          Disable endpoint");
    println!("  enable-endpoint <id> <ip>           Enable endpoint");
    println!("  set-config <key> <value>            Set configuration");
    println!("  help                                Show this help");
    println!("  exit                                Exit program");
    println!();
    println!("Command line only:");
    println!("  loop                                Start download loop");
    println!();
    println!("Config options (set-config <key> <value>):");
    println!("  ip-protocol <ipv4|ipv6|both>        IP protocol preference");
    println!("  connections <n>                     Total connections (default: 64)");
    println!("  retry <n>                           Retry count (default: 3)");
    println!("  chunk-size <size>                   Chunk size (default: 64KB)");
    println!("  speed-threshold <n>                 Speed threshold for allocation (default: 2.0)");
    println!("  download-timeout <time>             Download timeout (default: 30s)");
    println!("  dns-servers <servers>               DNS servers (comma-separated)");
    println!("  header <json>                       Set custom headers (JSON format)");
    println!("  remove-header <name>                Remove a custom header");
    println!("  clear-headers                       Clear all custom headers");
    println!("  proxy <key>=<url>                   Set proxy (see examples below)");
    println!("  remove-proxy <key>                  Remove a proxy rule");
    println!("  clear-proxy                         Clear all proxy rules");
    println!("  endpoint-failure-threshold <n>      Failures before disabling endpoint (default: 3)");
    println!("  retry-reconnect-count <n>           Reconnect retry count (default: 3)");
    println!("  retry-refetch-endpoint-count <n>    Refetch endpoint retry count (default: 2)");
    println!("  retry-refetch-url-count <n>         Refetch URL retry count (default: 1)");
    println!("  content-length-zero-retry <n>       Retries when Content-Length=0 (default: 3)");
    println!();
    println!("Proxy keys:");
    println!("  *                                   Global proxy for all connections");
    println!("  <ip>                                Proxy for specific IP (e.g., 1.2.3.4)");
    println!("  dns:*                               Proxy for all DNS queries");
    println!("  dns:<server>                        Proxy for specific DNS server");
    println!();
    println!("Examples:");
    println!("  set-config connections 16");
    println!("  set-config header '{{\"Authorization\": \"Bearer token\"}}'");
    println!("  set-config dns-servers '8.8.8.8,1.1.1.1'");
    println!("  set-config proxy *=http://127.0.0.1:10808");
    println!("  set-config proxy dns:*=http://127.0.0.1:10808");
    println!("  set-config proxy 1.2.3.4=http://127.0.0.1:10808");
    println!("  info all abc12345");
    println!();
    println!("Command line options:");
    println!("  --config <file>                     Load/save config from/to file");
    println!("  --json                              Output in JSON format");
    println!();
    println!("Config file example (rdownloader.json):");
    println!("  {{");
    println!("    \"ip_protocol\": \"OnlyIPv4\",");
    println!("    \"total_connections\": 64,");
    println!("    \"retry_count\": 3,");
    println!("    \"chunk_size\": 65536,");
    println!("    \"speed_threshold\": 2.0,");
    println!("    \"download_timeout_secs\": 30,");
    println!("    \"endpoint_failure_threshold\": 3,");
    println!("    \"retry_reconnect_count\": 3,");
    println!("    \"retry_refetch_endpoint_count\": 2,");
    println!("    \"retry_refetch_url_count\": 1,");
    println!("    \"content_length_zero_retry_count\": 3,");
    println!("    \"speed_test_retry_count\": 3,");
    println!("    \"speed_test_timeout_secs\": 15,");
    println!("    \"connectivity_timeout_secs\": 3,");
    println!("    \"dns_timeout_secs\": 5,");
    println!("    \"dns_retry_count\": 3,");
    println!("    \"scheduler_check_interval_secs\": 3,");
    println!("    \"scheduler_slow_threshold\": 0.3,");
    println!("    \"scheduler_reallocate_threshold\": 0.2,");
    println!("    \"dns_servers\": [\"system\", \"114.114.114.114\", \"https://223.5.5.5/dns-query\"],");
    println!("    \"headers\": {{");
    println!("      \"Authorization\": \"Bearer token\",");
    println!("      \"User-Agent\": \"MyApp/1.0\"");
    println!("    }},");
    println!("    \"proxy\": {{");
    println!("      \"*\": \"http://127.0.0.1:10808\",");
    println!("      \"dns:*\": \"http://127.0.0.1:10808\",");
    println!("      \"1.2.3.4\": \"socks5://127.0.0.1:10809\"");
    println!("    }}");
    println!("  }}");
}

/// 显示帮助信息（JSON 格式）
pub fn show_help_json(config_path: Option<&str>) {
    let mut commands = vec![
        "download <url> [--output <path>]", "list", "info <id>", "monitor <id>",
        "pause <id>", "resume <id>", "cancel <id>",
        "add-endpoint <id> <ip> [--proxy <p>]", "disable-endpoint <id> <ip>",
        "enable-endpoint <id> <ip>", "help", "exit",
    ];
    if config_path.is_some() {
        commands.push("set-config <key> <value>");
    }
    let commands_str = commands.iter().map(|c| format!("\"{}\"", c)).collect::<Vec<_>>().join(", ");
    println!(r#"{{"status": "ok", "help": [{}], "command_line_only": ["loop"]}}"#, commands_str);
}
