using System;

namespace ClassSoftwareHub.Desktop.SlideSync
{
    /// <summary>时段格式化的最小实现（「08:05」），设置页的文件夹时机文案用。</summary>
    public static class LessonWindow
    {
        public static string Fmt(TimeSpan t)
        {
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00");
        }
    }
}