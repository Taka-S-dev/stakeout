using System.Runtime.CompilerServices;

// COM を扱う内部クラスのうち、COM に触れない部分（VS の選択規則、
// データ式の検証など）は単体テストで確かめられる
[assembly: InternalsVisibleTo("Stakeout.Backend.EnvDte.Tests")]
