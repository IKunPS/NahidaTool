using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace NahidaTool.Models;

public static class Localization
{
    public static readonly ReadOnlyCollection<(string Title, string LangCode)> LanguageList =
        new(new List<(string, string)>
        {
            ("English (en-US)", "en-US"),
            ("简体中文 (zh-CN)", "zh-CN"),
        });
}