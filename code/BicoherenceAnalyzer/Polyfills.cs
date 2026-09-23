// .NET Framework 4.8 缺少 C# 9+ record/init 所需的 IsExternalInit 类型
// 参考: https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-9.0/init

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
