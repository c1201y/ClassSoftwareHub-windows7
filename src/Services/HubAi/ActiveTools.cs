using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 「这次对话能用哪些工具」的唯一判定处。
///
/// <para>
/// 来由：Hub AI 的入口从完整页扩展到了软件下载 / AppData / 卸载等页面的浮层。
/// 工具清单要是各写一份，「联网」或「工具总闸」改了开关，就会出现"页面里有工具、浮层里没有"
/// 这种查不出来的怪事。
/// </para>
///
/// <para>
/// 联网工具与「工具总闸」**互相独立**：总闸关着，只要用户把「联网」拨上就能用
/// （与「思考」开关的行为一致）。
/// </para>
/// </summary>
public static class ActiveTools
{
    /// <summary>按当前设置返回可用工具（顺序即提示词里的顺序）。</summary>
    public static IReadOnlyList<ToolDefinition> Load()
    {
        var tools = new List<ToolDefinition>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);

        // 按 id 去重。优先级：**后加入的同 id 定义覆盖先加入的** —— 内置工具是随程序审计过、
        // 与宿主执行白名单一一对应的权威声明，不应被可远程更新的工具包里同名项遮蔽或改写，
        // 否则模型会看到两条互相矛盾的同名工具。位置保留首次出现处（稳定去重），
        // 维持原有「pack → Docs → 动作 → Web」的顺序语义。
        void AddOne(ToolDefinition def)
        {
            var id = def.Id;
            // 空 id 既无从去重也调不动，原样加入，不改变原有行为
            if (string.IsNullOrEmpty(id))
            {
                tools.Add(def);
                return;
            }

            if (indexById.TryGetValue(id, out var index))
                tools[index] = def;   // 后到覆盖，位置不变
            else
            {
                indexById[id] = tools.Count;
                tools.Add(def);
            }
        }

        void Add(IEnumerable<ToolDefinition> defs)
        {
            foreach (var def in defs) AddOne(def);
        }

        if (App.Settings.Current.HubAiToolsEnabled)
        {
            var pack = ToolPackStore.Load();
            if (pack?.Tools is { Count: > 0 } packTools) Add(packTools);

            // 文档库（需求 1:15）：看清单 / 读正文。随程序内置，不依赖工具包。
            Add(BuiltinTools.Docs);

            // 需求 27：回答末尾的可点按钮。**不依赖工具包** —— 它随程序内置，
            // 工具包没装成（或下载失败）时这一项照样能用。
            // ⚠️ 跟着「工具总闸」走：总闸在用户那里的含义就是"让不让模型调工具"，
            //    关掉之后连按钮也不要出现，免得出现"关了还照常"的矛盾。
            AddOne(AiActionCatalog.ToolDefinition);
        }

        if (App.Settings.Current.HubAiWebEnabled) Add(BuiltinTools.Web);

        return tools;
    }
}
