using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentPoc.Core;

/// <summary>Log de console colorido, indentado pela profundidade da delegacao.</summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Silencia o log detalhado do harness (o chat mostra os eventos do seu jeito). Avisos e erros continuam.</summary>
    public static bool Muted { get; set; }

    public static void Write(int depth, ConsoleColor color, string tag, string message)
    {
        if (!Muted) Emit(depth, color, tag, message);
    }

    private static void Emit(int depth, ConsoleColor color, string tag, string message)
    {
        var indent = new string(' ', Math.Max(0, depth) * 4);
        var oneLine = (message ?? "").Replace("\r", "").Replace("\n", " | ");
        if (oneLine.Length > 240) oneLine = oneLine.Substring(0, 240) + "...";
        lock (Gate)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{DateTime.Now:HH:mm:ss.fff} {indent}");
            Console.ForegroundColor = color;
            Console.Write($"[{tag}] ");
            Console.ResetColor();
            Console.WriteLine(oneLine);
        }
    }

    public static void Info(string m) => Write(0, ConsoleColor.Cyan, "info", m);
    public static void Ok(string m) => Write(0, ConsoleColor.Green, "ok", m);
    public static void Warn(string m) => Emit(0, ConsoleColor.Yellow, "aviso", m);
    public static void Error(string m) => Emit(0, ConsoleColor.Red, "erro", m);

    /// <summary>Escreve sem quebra de linha (texto em streaming do chat).</summary>
    public static void Inline(string m, ConsoleColor? color = null)
    {
        lock (Gate)
        {
            if (color.HasValue) Console.ForegroundColor = color.Value;
            Console.Write(m);
            if (color.HasValue) Console.ResetColor();
        }
    }

    public static void Title(string m)
    {
        lock (Gate)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"==== {m} ====");
            Console.ResetColor();
        }
    }

    public static void Raw(string m)
    {
        lock (Gate) Console.WriteLine(m);
    }
}

public static class Text
{
    /// <summary>Serializacao sem escapar acentos e simbolos (logs e evidencias legiveis).</summary>
    public static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + $"... [+{s.Length - max} chars]";
    }

    public static string Short(Guid id) => id.ToString("N").Substring(0, 8);
}
