using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ADOFAIEditorStudio.Tests
{
    /// <summary>
    /// `EditorProjectFile` 的读写保真测试。
    ///
    /// **"保真"指什么：** 这个类存的是「事件身份 → 行号」和「模块多边形」，
    /// 都是玩家摆一晚上攒出来的布局。来回存读一次只要丢一样东西，
    /// 用户看到的就是"重开谱面排版全乱了"。所以这里逐条验证：
    ///
    ///   1. 路径推导（工程目录 / 找文件）
    ///   2. 空工程 / 没有文件 / 坏文件的兜底
    ///   3. 完整往返：行号表 + 模块（名字 / 顶点 / 折叠状态）
    ///   4. 浮点保真（模块顶点是坐标，掉精度就是框变形）
    ///   5. 中文名、特殊字符
    ///   6. 旧格式兼容（没有 Modules 字段的工程）
    ///
    /// 跑法：`dotnet run --project tests/ProjectFileTests`
    /// 退出码 0 = 全过，1 = 有失败。
    /// </summary>
    internal static class Program
    {
        private static int failures;
        private static int checks;

        private static int Main(string[] args)
        {
            // 控制台默认是系统代码页，中文会显示成乱码。切到 UTF-8 ——
            // 这是唯一能让失败信息可读的办法（不然只能看到问号）。
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 某些终端不支持改编码，忽略就好，不影响测试结果。
            }

            string root = Path.Combine(Path.GetTempPath(),
                "AES_ProjectFileTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TestGetProjectDirectory(root);
                TestFindProjectFile(root);
                TestLoadMissingFile(root);
                TestRoundTripBasic(root);
                TestRoundTripFloatPrecision(root);
                TestRoundTripChineseAndSpecialChars(root);
                TestLegacyFileWithoutModules(root);
                TestMalformedModulesAreDropped(root);
                TestEmptyProject(root);
                TestSaveCreatesDirectory(root);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception exception)
                {
                    Console.WriteLine("[warn] 清理临时目录失败：" + exception.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine("检查 " + checks + " 项，失败 " + failures + " 项");
            return failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ 断言

        private static void Check(bool condition, string what)
        {
            checks++;
            if (condition)
            {
                return;
            }
            failures++;
            Console.WriteLine("[FAIL] " + what);
        }

        private static void CheckEqual<T>(T expected, T actual, string what)
        {
            checks++;
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                return;
            }
            failures++;
            Console.WriteLine("[FAIL] " + what
                + "  期望=" + expected + "  实际=" + actual);
        }

        private static void CheckNear(float expected, float actual, float tolerance,
            string what)
        {
            checks++;
            if (Math.Abs(expected - actual) <= tolerance)
            {
                return;
            }
            failures++;
            Console.WriteLine("[FAIL] " + what
                + "  期望=" + expected.ToString("R")
                + "  实际=" + actual.ToString("R")
                + "  差=" + Math.Abs(expected - actual).ToString("R"));
        }

        // ------------------------------------------------------------ 路径

        private static void TestGetProjectDirectory(string root)
        {
            string chart = Path.Combine(root, "songs", "MyChart.adofai");
            string expected = Path.Combine(root, "songs", "MyChart_AES");
            CheckEqual(expected, EditorProjectFile.GetProjectDirectory(chart),
                "工程目录 = <谱面目录>/<谱面名>_AES");

            // 没有目录部分时要能兜住（不能抛）。
            CheckEqual(Path.Combine(".", "x_AES"),
                EditorProjectFile.GetProjectDirectory("x.adofai"),
                "空目录部分兜底成 .");

            // 没有文件名也要兜住。
            Check(!string.IsNullOrEmpty(EditorProjectFile.GetProjectDirectory("")),
                "空路径不返回空串");
        }

        private static void TestFindProjectFile(string root)
        {
            string chart = Path.Combine(root, "find", "A.adofai");
            Check(EditorProjectFile.FindProjectFile(chart) == null,
                "工程目录不存在时返回 null");

            string directory = EditorProjectFile.GetProjectDirectory(chart);
            Directory.CreateDirectory(directory);
            Check(EditorProjectFile.FindProjectFile(chart) == null,
                "工程目录空时返回 null");

            string path = Path.Combine(directory, "any.aesprj");
            File.WriteAllText(path, "{}", new UTF8Encoding(false));
            CheckEqual(path, EditorProjectFile.FindProjectFile(chart),
                "找到目录里第一个 .aesprj");
        }

        private static void TestLoadMissingFile(string root)
        {
            EditorProjectFile.Project project;
            Check(!EditorProjectFile.TryLoad(
                    Path.Combine(root, "nope", "none.aesprj"), out project),
                "文件不存在返回 false");
            Check(project == null, "文件不存在时 project 是 null");

            Check(!EditorProjectFile.TryLoad(null, out project),
                "路径为 null 返回 false");
            Check(!EditorProjectFile.TryLoad("", out project),
                "路径为空返回 false");

            string broken = Path.Combine(root, "broken.aesprj");
            File.WriteAllText(broken, "{ this is not json ", new UTF8Encoding(false));
            Check(!EditorProjectFile.TryLoad(broken, out project),
                "坏 JSON 返回 false（不抛）");
        }

        // ------------------------------------------------------------ 往返

        private static void TestRoundTripBasic(string root)
        {
            string path = Path.Combine(root, "basic", "p.aesprj");
            var project = new EditorProjectFile.Project { Chart = "MyChart.adofai" };
            project.Rows["24:6:4.3820:angleOffset=0|filter=Aberration"] = 7;
            project.Rows["7:12:1.5000:"] = 3;

            var module = new EditorProjectFile.ProjectModule
            {
                Name = "灯光组",
                Collapsed = true
            };
            module.PointsX.Add(1.5f);
            module.PointsY.Add(2f);
            module.PointsX.Add(4.25f);
            module.PointsY.Add(2f);
            module.PointsX.Add(4.25f);
            module.PointsY.Add(6.5f);
            module.PointsX.Add(1.5f);
            module.PointsY.Add(6.5f);
            project.Modules.Add(module);

            string error = EditorProjectFile.Save(path, project);
            CheckEqual("", error, "保存返回空串表示成功");
            Check(File.Exists(path), "保存后文件存在");

            EditorProjectFile.Project loaded;
            Check(EditorProjectFile.TryLoad(path, out loaded), "读回成功");
            if (loaded == null)
            {
                return;
            }

            CheckEqual(EditorProjectFile.CurrentFormatVersion, loaded.Version,
                "格式版本随存盘刷新");
            CheckEqual("MyChart.adofai", loaded.Chart, "谱面名保真");
            CheckEqual(2, loaded.Rows.Count, "行号表条数保真");
            Check(loaded.Rows.ContainsKey("24:6:4.3820:angleOffset=0|filter=Aberration"),
                "含特殊字符的身份键保真");
            CheckEqual(7, loaded.Rows["24:6:4.3820:angleOffset=0|filter=Aberration"],
                "身份键对应的行号保真");
            CheckEqual(3, loaded.Rows["7:12:1.5000:"], "第二条行号保真");

            CheckEqual(1, loaded.Modules.Count, "模块数保真");
            if (loaded.Modules.Count == 1)
            {
                EditorProjectFile.ProjectModule back = loaded.Modules[0];
                CheckEqual("灯光组", back.Name, "模块名保真");
                Check(back.Collapsed, "折叠状态保真");
                CheckEqual(4, back.PointsX.Count, "顶点数保真（X）");
                CheckEqual(4, back.PointsY.Count, "顶点数保真（Y）");
                CheckNear(1.5f, back.PointsX[0], 0f, "顶点 X[0] 精确");
                CheckNear(4.25f, back.PointsX[1], 0f, "顶点 X[1] 精确");
                CheckNear(6.5f, back.PointsY[3], 0f, "顶点 Y[3] 精确");
            }
        }

        private static void TestRoundTripFloatPrecision(string root)
        {
            string path = Path.Combine(root, "float", "p.aesprj");
            var project = new EditorProjectFile.Project();
            var module = new EditorProjectFile.ProjectModule();
            // 这些值以前在 JSON 往返里最容易掉精度。
            float[] xs = { 3.201651E-06f, 0.1f, 1234.5678f, -0.000123f, 1f / 3f };
            float[] ys = { 1.0000001f, 2.5f, 0.333333343f, -7.25f, 0f };
            for (int i = 0; i < xs.Length; i++)
            {
                module.PointsX.Add(xs[i]);
                module.PointsY.Add(ys[i]);
            }
            project.Modules.Add(module);

            CheckEqual("", EditorProjectFile.Save(path, project), "保存成功");
            EditorProjectFile.Project loaded;
            Check(EditorProjectFile.TryLoad(path, out loaded), "读回成功");
            if (loaded == null || loaded.Modules.Count != 1)
            {
                Check(false, "浮点用例：模块没读回来");
                return;
            }
            for (int i = 0; i < xs.Length; i++)
            {
                CheckNear(xs[i], loaded.Modules[0].PointsX[i], 0f,
                    "浮点 X[" + i + "] 逐位保真");
                CheckNear(ys[i], loaded.Modules[0].PointsY[i], 0f,
                    "浮点 Y[" + i + "] 逐位保真");
            }
        }

        private static void TestRoundTripChineseAndSpecialChars(string root)
        {
            string path = Path.Combine(root, "chars", "p.aesprj");
            var project = new EditorProjectFile.Project
            {
                Chart = "测试谱面 \"引号\" / 斜杠 \\ 反斜杠.adofai"
            };
            project.Rows["键 里 有 空 格"] = 0;
            project.Rows["emoji? No. 制表\t符号"] = 1;
            var module = new EditorProjectFile.ProjectModule { Name = "模块「一」" };
            module.PointsX.AddRange(new[] { 0f, 1f, 1f });
            module.PointsY.AddRange(new[] { 0f, 0f, 1f });
            project.Modules.Add(module);

            CheckEqual("", EditorProjectFile.Save(path, project), "保存成功");
            EditorProjectFile.Project loaded;
            Check(EditorProjectFile.TryLoad(path, out loaded), "读回成功");
            if (loaded == null)
            {
                return;
            }
            CheckEqual("测试谱面 \"引号\" / 斜杠 \\ 反斜杠.adofai", loaded.Chart,
                "谱面名里的引号和反斜杠保真");
            Check(loaded.Rows.ContainsKey("键 里 有 空 格"), "中文键保真");
            Check(loaded.Rows.ContainsKey("emoji? No. 制表\t符号"), "制表符键保真");
            CheckEqual(1, loaded.Modules.Count, "中文模块读回");
            if (loaded.Modules.Count == 1)
            {
                CheckEqual("模块「一」", loaded.Modules[0].Name, "中文模块名保真");
            }

            // 文件必须是 UTF-8 且不带 BOM（类里明确说了不带）。
            byte[] head = new byte[3];
            using (FileStream stream = File.OpenRead(path))
            {
                stream.Read(head, 0, 3);
            }
            Check(!(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF),
                "写出来的文件不带 UTF-8 BOM");
        }

        // ------------------------------------------------------------ 兼容 / 容错

        private static void TestLegacyFileWithoutModules(string root)
        {
            string path = Path.Combine(root, "legacy", "p.aesprj");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // 老版本：只有 Version / Chart / Rows，没有 Modules。
            // Version 留成 1 是为了顺带验证"老版本号也读得进来"。
            File.WriteAllText(path,
                "{\n  \"Version\": 1,\n  \"Chart\": \"old.adofai\",\n"
                + "  \"Rows\": { \"a:b:c:d\": 5 }\n}",
                new UTF8Encoding(false));

            EditorProjectFile.Project loaded;
            Check(EditorProjectFile.TryLoad(path, out loaded), "老文件读得进来");
            if (loaded == null)
            {
                return;
            }
            CheckEqual(1, loaded.Rows.Count, "老文件行号保真");
            Check(loaded.Modules != null, "没有 Modules 字段时补成空表");
            CheckEqual(0, loaded.Modules.Count, "老文件模块表为空");
        }

        private static void TestMalformedModulesAreDropped(string root)
        {
            string path = Path.Combine(root, "malformed", "p.aesprj");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // 故意塞三种坏模块：顶点不足三个、X/Y 长度不等、null。
            string text =
                "{\n"
                + "  \"Version\": 2,\n"
                + "  \"Rows\": {},\n"
                + "  \"Modules\": [\n"
                + "    { \"Name\": \"好\", \"PointsX\": [0,1,1], \"PointsY\": [0,0,1] },\n"
                + "    { \"Name\": \"两点\", \"PointsX\": [0,1], \"PointsY\": [0,0] },\n"
                + "    { \"Name\": \"不等长\", \"PointsX\": [0,1,1], \"PointsY\": [0,0] },\n"
                + "    null\n"
                + "  ]\n"
                + "}";
            File.WriteAllText(path, text, new UTF8Encoding(false));

            EditorProjectFile.Project loaded;
            Check(EditorProjectFile.TryLoad(path, out loaded), "含坏模块的文件读得进来");
            if (loaded == null)
            {
                return;
            }
            CheckEqual(1, loaded.Modules.Count, "坏模块被剔除，只剩好的那个");
            if (loaded.Modules.Count == 1)
            {
                CheckEqual("好", loaded.Modules[0].Name, "留下的是好的那个");
            }
        }

        private static void TestEmptyProject(string root)
        {
            string path = Path.Combine(root, "empty", "p.aesprj");
            var project = new EditorProjectFile.Project();
            CheckEqual("", EditorProjectFile.Save(path, project), "空工程保存成功");

            EditorProjectFile.Project loaded;
            Check(EditorProjectFile.TryLoad(path, out loaded), "空工程读回成功");
            if (loaded == null)
            {
                return;
            }
            CheckEqual(0, loaded.Rows.Count, "空工程行号表为空");
            CheckEqual(0, loaded.Modules.Count, "空工程模块表为空");
        }

        private static void TestSaveCreatesDirectory(string root)
        {
            string path = Path.Combine(root, "deep", "a", "b", "c", "p.aesprj");
            var project = new EditorProjectFile.Project();
            CheckEqual("", EditorProjectFile.Save(path, project), "多层目录也能存");
            Check(File.Exists(path), "递归建目录后文件存在");

            Check(!string.IsNullOrEmpty(EditorProjectFile.Save("", project)),
                "空路径保存返回失败原因");
            Check(!string.IsNullOrEmpty(EditorProjectFile.Save(path, null)),
                "project 为 null 时返回失败原因");
        }
    }
}
