using System;

namespace ClassSoftwareHub.Desktop.SlideSync;

/// <summary>
/// 一次记录开始时的“这是哪一节课”的读数。CSH 没有 ClassIsland 的课表服务，
/// 由 PPT 续播服务从导入的 CSES 课表里填科目与周几；其余字段留空即可。
/// </summary>
public sealed class LessonTag
{
    public string Subject = "";
    public string LessonName = "";
    public string PeriodName = "";
    public int Period;
    public int Weekday;

    public string Context
    {
        get
        {
            if (Subject.Length == 0) return "";
            return LessonName.Length > 0 ? Subject + " · " + LessonName : Subject;
        }
    }
}