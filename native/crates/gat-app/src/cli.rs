use std::path::PathBuf;

use clap::{Parser, Subcommand};

#[derive(Parser, Debug)]
#[command(
    name = "gat",
    version,
    about = "游戏时长记录器 Rust 后台轻量引擎（无主窗口、低内存）"
)]
pub struct Cli {
    /// 以托盘后台轻量模式运行（默认）
    #[arg(long, global = true)]
    pub background: bool,
    /// 无托盘、无界面运行
    #[arg(long, global = true)]
    pub headless: bool,
    /// 运行有限时长的启动冒烟测试后退出
    #[arg(long, global = true)]
    pub smoke_test: bool,
    /// 冒烟测试时长（秒）
    #[arg(long, global = true)]
    pub smoke_seconds: Option<u64>,
    /// 覆盖数据目录（默认 %LOCALAPPDATA%\GameActivityTracker）
    #[arg(long, global = true)]
    pub data_dir: Option<PathBuf>,
    /// 最轻量配置：关闭关联可执行文件目录扫描
    #[arg(long, global = true)]
    pub lightweight: bool,
    #[command(subcommand)]
    pub command: Option<Command>,
}

#[derive(Subcommand, Debug)]
pub enum Command {
    /// 查询后台运行状态与当前游戏
    Status,
    /// 查询当前正在运行的游戏
    Snapshot,
    /// 列出已登记游戏
    Games,
    /// 输出本年度统计
    Stats,
    /// 让后台进程重新加载规则与设置
    Reload,
    /// 暂停记录
    Suspend,
    /// 恢复记录
    Resume,
    /// 退出后台进程
    Shutdown,
}
