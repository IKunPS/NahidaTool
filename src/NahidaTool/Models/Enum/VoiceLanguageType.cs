using System;

namespace NahidaTool.Models.Enum;

/// <summary>
/// 语音包语言枚举
/// </summary>
[Flags]
public enum VoiceLanguageType
{
    None = 0,
    Chinese = 1,
    English = 2,
    Japanese = 4,
    Korean = 8
}