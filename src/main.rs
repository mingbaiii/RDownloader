mod cli;
mod config;
mod dns;
mod downloader;
mod endpoint_test;
mod remote_file;
mod utils;

use std::io::{self, BufRead, Write};

use cli::parser::{parse_args, parse_interactive_command, CliConfig, Command};
use cli::commands::execute_command;
use rustyline::completion::{Completer, Pair};
use rustyline::error::ReadlineError;
use rustyline::{Editor, Context, Helper};
use rustyline::highlight::Highlighter;
use rustyline::hint::Hinter;
use rustyline::validate::Validator;

/// Tab 补全器
struct RdownCompleter;

impl Completer for RdownCompleter {
    type Candidate = Pair;

    fn complete(
        &self,
        line: &str,
        pos: usize,
        _ctx: &Context<'_>,
    ) -> Result<(usize, Vec<Pair>), ReadlineError> {
        let commands = [
            "download", "list", "info", "monitor", "pause", "resume",
            "cancel", "add-endpoint", "disable-endpoint", "enable-endpoint",
            "set-config", "help", "exit", "loop",
        ];

        // 获取当前输入的单词
        let start = line[..pos].rfind(' ').map(|i| i + 1).unwrap_or(0);
        let word = &line[start..];

        let mut matches = Vec::new();

        // 解析命令和参数（保留空字符串以检测尾部空格）
        let parts: Vec<&str> = line[..pos].split(' ').collect();
        let cmd = parts.first().copied().unwrap_or("");
        let arg_count = parts.len();

        // 判断是否在输入参数（有尾部空格表示正在输入下一个参数）
        let in_args = line[..pos].ends_with(' ') || arg_count > 1;

        if !in_args {
            // 第一个单词，补全命令
            for c in &commands {
                if c.starts_with(word) {
                    matches.push(Pair {
                        display: c.to_string(),
                        replacement: c.to_string(),
                    });
                }
            }
        } else {
            // 根据命令补全参数
            match cmd {
                "set-config" => {
                    if arg_count == 2 && !line[..pos].ends_with(' ') {
                        // 正在输入配置项名称
                        let config_keys = [
                            "ip-protocol", "connections", "retry",
                            "chunk-size", "speed-threshold", "download-timeout",
                            "dns-servers", "header", "headers", "remove-header", "clear-headers",
                            "proxy", "remove-proxy", "clear-proxy",
                            "endpoint-failure-threshold", "retry-reconnect-count",
                            "retry-refetch-endpoint-count", "retry-refetch-url-count",
                            "content-length-zero-retry",
                        ];
                        for key in &config_keys {
                            if key.starts_with(word) {
                                matches.push(Pair {
                                    display: key.to_string(),
                                    replacement: key.to_string(),
                                });
                            }
                        }
                    } else if arg_count == 2 && line[..pos].ends_with(' ') {
                        // 配置项已输入，准备输入配置值
                        let config_keys = [
                            "ip-protocol", "connections", "retry",
                            "chunk-size", "speed-threshold", "download-timeout",
                            "dns-servers", "header", "headers", "remove-header", "clear-headers",
                            "proxy", "remove-proxy", "clear-proxy",
                            "endpoint-failure-threshold", "retry-reconnect-count",
                            "retry-refetch-endpoint-count", "retry-refetch-url-count",
                            "content-length-zero-retry",
                        ];
                        for key in &config_keys {
                            matches.push(Pair {
                                display: key.to_string(),
                                replacement: key.to_string(),
                            });
                        }
                    } else if arg_count == 3 {
                        // 正在输入配置值
                        let key = parts.get(1).copied().unwrap_or("");
                        let values = match key {
                            "ip-protocol" => vec!["ipv4", "ipv6", "both"],
                            "connections" => vec!["4", "8", "16", "32", "64", "128"],
                            "retry" => vec!["1", "3", "5"],
                            "chunk-size" => vec!["64KB", "1MB", "4MB"],
                            "speed-threshold" => vec!["1.5", "2.0", "3.0"],
                            "download-timeout" => vec!["30s", "1m", "5m"],
                            "dns-servers" => vec!["system,114.114.114.114,https://223.5.5.5/dns-query"],
                            "header" | "headers" => vec![r#"{"Authorization": "Bearer token"}"#],
                            "remove-header" => vec!["Authorization", "User-Agent"],
                            "clear-headers" => vec![],
                            "proxy" => vec!["*=http://127.0.0.1:10808", "dns:*=http://127.0.0.1:10808"],
                            "remove-proxy" => vec!["*", "dns:*"],
                            "clear-proxy" => vec![],
                            "endpoint-failure-threshold" => vec!["3", "5", "10"],
                            "retry-reconnect-count" => vec!["1", "3", "5"],
                            "retry-refetch-endpoint-count" => vec!["1", "2", "3"],
                            "retry-refetch-url-count" => vec!["1", "2", "3"],
                            "content-length-zero-retry" => vec!["3", "5", "10"],
                            _ => vec![],
                        };
                        for val in values {
                            if val.starts_with(word) {
                                matches.push(Pair {
                                    display: val.to_string(),
                                    replacement: val.to_string(),
                                });
                            }
                        }
                    }
                }
                "info" | "monitor" | "pause" | "resume" | "cancel" => {
                    // 补全下载 ID
                    let downloads = cli::state::load_all_downloads();
                    for download in &downloads {
                        if download.id.starts_with(word) {
                            matches.push(Pair {
                                display: format!("{} ({})", download.id, download.url),
                                replacement: download.id.clone(),
                            });
                        }
                    }
                }
                "add-endpoint" | "disable-endpoint" | "enable-endpoint" => {
                    // 补全下载 ID（第一个参数）
                    if parts.len() == 2 {
                        let downloads = cli::state::load_all_downloads();
                        for download in &downloads {
                            if download.id.starts_with(word) {
                                matches.push(Pair {
                                    display: format!("{} ({})", download.id, download.url),
                                    replacement: download.id.clone(),
                                });
                            }
                        }
                    }
                }
                _ => {}
            }
        }

        Ok((start, matches))
    }
}

impl Highlighter for RdownCompleter {}
impl Hinter for RdownCompleter {
    type Hint = String;
}
impl Validator for RdownCompleter {}
impl Helper for RdownCompleter {}

#[tokio::main]
async fn main() {
    let (mut config, command) = parse_args();

    // 如果没有指定配置文件，自动检测当前目录下的默认配置文件
    if config.config_path.is_none() {
        let default_names = ["rdownloader.json", "rdown.json"];
        if let Ok(cwd) = std::env::current_dir() {
            for name in &default_names {
                let path = cwd.join(name);
                if path.exists() {
                    config.config_path = Some(path);
                    break;
                }
            }
        }
    }

    // 初始化配置
    crate::config::init_config(config.config_path.clone()).await;

    // 如果有直接命令，执行后退出
    if let Some(cmd) = command {
        execute_command(cmd, &config).await;
        return;
    }

    // 启动时暂停所有 downloading 状态的任务（仅用户交互模式）。
    // JSON/Program 模式由 GUI 控制生命周期，每个进程只管理一个任务，
    // 不应触碰其他进程正在运行的任务状态。
    if !config.json_mode {
        pause_downloading_on_startup(&config);
    }

    // 交互模式
    if config.json_mode {
        // 程序模式：从 stdin 读取命令
        run_program_mode(&config).await;
    } else {
        // 用户模式：显示提示符
        run_user_mode(&config).await;
    }
}

/// 启动时暂停所有 downloading 状态的任务
fn pause_downloading_on_startup(config: &cli::parser::CliConfig) {
    use cli::state::{self, DownloadStatus};

    let downloads = state::load_all_downloads();
    let mut paused_count = 0;

    for mut info in downloads {
        if info.status == DownloadStatus::Downloading {
            info.status = DownloadStatus::Paused;
            if state::save_download(&info).is_ok() {
                paused_count += 1;
            }
        }
    }

    if !config.json_mode && paused_count > 0 {
        println!("Paused {} incomplete download(s)", paused_count);
    }
}

/// 用户模式
async fn run_user_mode(config: &CliConfig) {
    let mut rl = Editor::new().unwrap();
    rl.set_helper(Some(RdownCompleter));

    loop {
        let readline = rl.readline("rdown> ");
        match readline {
            Ok(line) => {
                let input = line.trim();
                if input.is_empty() {
                    continue;
                }

                // 添加到历史记录
                rl.add_history_entry(input).ok();

                match parse_interactive_command(input) {
                    Ok(cmd) => {
                        if let Command::Exit = cmd {
                            break;
                        }
                        execute_command(cmd, config).await;
                    }
                    Err(e) => {
                        eprintln!("Error: {}", e);
                    }
                }
            }
            Err(ReadlineError::Interrupted) => {
                // Ctrl+C
                continue;
            }
            Err(ReadlineError::Eof) => {
                // Ctrl+D
                break;
            }
            Err(e) => {
                eprintln!("Error: {}", e);
                break;
            }
        }
    }
}

/// 程序模式
async fn run_program_mode(config: &CliConfig) {
    let stdin = io::stdin();

    for line in stdin.lock().lines() {
        match line {
            Ok(input) => {
                let input = input.trim();
                if input.is_empty() {
                    continue;
                }

                // 尝试解析 JSON
                match serde_json::from_str::<serde_json::Value>(input) {
                    Ok(json) => {
                        if let Some(cmd_str) = json.get("command").and_then(|v| v.as_str()) {
                            let args: Vec<String> = match json.get("args") {
                                Some(serde_json::Value::Array(arr)) => {
                                    arr.iter().filter_map(|v| v.as_str().map(|s| s.to_string())).collect()
                                }
                                _ => Vec::new(),
                            };

                            let cmd_input = if args.is_empty() {
                                cmd_str.to_string()
                            } else {
                                format!("{} {}", cmd_str, args.join(" "))
                            };

                            match parse_interactive_command(&cmd_input) {
                                Ok(cmd) => {
                                    if let Command::Exit = cmd {
                                        break;
                                    }
                                    execute_command(cmd, config).await;
                                }
                                Err(e) => {
                                    println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                                }
                            }
                        } else {
                            println!(r#"{{"status": "error", "message": "Missing 'command' field"}}"#);
                        }
                    }
                    Err(_) => {
                        // 不是 JSON，尝试作为普通命令解析
                        match parse_interactive_command(input) {
                            Ok(cmd) => {
                                if let Command::Exit = cmd {
                                    break;
                                }
                                execute_command(cmd, config).await;
                            }
                            Err(e) => {
                                println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                            }
                        }
                    }
                }
            }
            Err(e) => {
                println!(r#"{{"status": "error", "message": "{}"}}"#, e);
                break;
            }
        }
    }
}
