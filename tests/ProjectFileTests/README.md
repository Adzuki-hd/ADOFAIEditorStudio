# EditorProjectFile 读写保真测试

独立测试工程，**不进 mod 程序集**。

```bash
dotnet run --project tests/ProjectFileTests            # 跑
dotnet build tests/ProjectFileTests -c Release          # 只编
```

退出码 `0` = 全过，`1` = 有失败。

## 为什么不放进主工程

主工程是 `net48` 的 **UMM mod 库**，一编译就要引用整套 Unity + 游戏程序集，
而且输出会进 `Mods/`。测试不该有这些依赖，也不该混进发布包。

做法是 `Compile Include` 把被测文件单独拉进一个控制台工程：

```xml
<Compile Include="..\..\EditorProjectFile.cs" Link="EditorProjectFile.cs" />
```

主工程那边加一行 `<Compile Remove="tests\**" />`，两边互不干扰。

## 需要顶替的依赖

`EditorProjectFile` 只依赖 `Main.Logger`（三个日志调用）。测试里用
`FakeMain.cs` 提供一个同名同签名的最小实现，把内容打到控制台 ——
这样不用为了三行日志把 UnityModManager 拖进来。

## NuGet 的 Newtonsoft 而不是游戏那份

游戏自带的 `Newtonsoft.Json.dll` 是**强命名**的，测试 exe 没签名，
运行时 `FileLoadException: 强名称验证失败`。NuGet 的 13.0.3 是同一大版本、
API 一致，被测类也没用任何自定义 converter —— 序列化行为一样。

## 覆盖了什么

| 分组 | 内容 |
|---|---|
| 路径推导 | 工程目录、找文件、空路径 / 空文件名的兜底 |
| 读失败兜底 | 文件不存在、路径 null / 空、坏 JSON（都不能抛） |
| 完整往返 | 行号表、模块名、顶点、折叠状态 |
| 浮点保真 | `3.201651E-06`、`1f/3f` 这类最容易掉的精度 |
| 特殊字符 | 中文、引号、反斜杠、制表符；顺带验证文件不带 BOM |
| 旧格式兼容 | 没有 `Modules` 字段的老工程 |
| 坏模块剔除 | 顶点不足三个、X/Y 不等长、null 条目 |
| 写盘边界 | 空工程、多层目录递归创建、空路径 / null project 报错 |

## 已经抓到过的真 bug

**`GetProjectDirectory("")` 抛 `ArgumentException`。**
`Path.GetDirectoryName("")` 在 .NET Framework 4.8 上抛异常而不是返回 null，
而"谱面路径为空"是**正常情况**（场景切换的中间态）。已加空串前置判断。
