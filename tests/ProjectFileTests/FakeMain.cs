using System;

namespace ADOFAIEditorStudio
{
    /// <summary>
    /// 测试用的假 `Main` —— **只为了让 `EditorProjectFile` 编得过**。
    ///
    /// 真实那个 `Main` 挂在 UnityModManager 上（`ModLogger` 是 UMM 的类型），
    /// 测试工程不该为了三个日志调用就把 UMM 拖进来。这里用一套同名同签名的
    /// 最小实现顶替：`Logger` 是个自己能接 `Log/Warning/Error` 的对象，
    /// 被调用就把内容打到控制台（测试跑的时候能看见失败原因）。
    ///
    /// **这不影响主工程**：主工程有自己的 Main.cs，不会编到这一个。
    /// </summary>
    internal static class Main
    {
        internal static readonly TestLogger Logger = new TestLogger();
    }

    /// <summary>
    /// 最小的日志器，签名对着 UMM 的 `ModLogger`（Log / Warning / Error）。
    /// </summary>
    internal sealed class TestLogger
    {
        internal void Log(string message)
        {
            Console.WriteLine("[log] " + message);
        }

        internal void Warning(string message)
        {
            Console.WriteLine("[warn] " + message);
        }

        internal void Error(string message)
        {
            Console.WriteLine("[error] " + message);
        }
    }
}
