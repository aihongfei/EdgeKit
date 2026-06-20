using System;
using EdgeKit.App.ViewModels;
using EdgeKit.Core.Tasks;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EdgeKit.App.Views;

public sealed class TaskCardPriorityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush HighBrush = new(Color.FromArgb(0xFF, 0xEF, 0x53, 0x50));
    private static readonly SolidColorBrush NormalBrush = new(Color.FromArgb(0xFF, 0xFF, 0xCA, 0x28));
    private static readonly SolidColorBrush LowBrush = new(Color.FromArgb(0xFF, 0x42, 0xA5, 0xF5));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is TaskCardPriority priority
            ? priority switch
            {
                TaskCardPriority.High => HighBrush,
                TaskCardPriority.Low => LowBrush,
                _ => NormalBrush
            }
            : DefaultBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class TimeBucketToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OverdueBrush = new(Color.FromArgb(0xFF, 0xEF, 0x53, 0x50));
    private static readonly SolidColorBrush TodayBrush = new(Color.FromArgb(0xFF, 0xFF, 0xCA, 0x28));
    private static readonly SolidColorBrush ThisWeekBrush = new(Color.FromArgb(0xFF, 0x42, 0xA5, 0xF5));
    private static readonly SolidColorBrush LaterBrush = new(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string bucket
            ? bucket switch
            {
                "已逾期" => OverdueBrush,
                "今天到期" => TodayBrush,
                "本周到期" => ThisWeekBrush,
                "更晚" => LaterBrush,
                _ => DefaultBrush
            }
            : DefaultBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class OverdueToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OverdueBrush = new(Color.FromArgb(0xFF, 0xEF, 0x53, 0x50));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? OverdueBrush : DefaultBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class TaskCardStatusToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is TaskCardStatus status ? (int)status : 0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is int index ? (TaskCardStatus)index : TaskCardStatus.Todo;
}

public sealed class TaskCardStatusToBarBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OverdueBrush = new(Color.FromArgb(0xFF, 0xEF, 0x53, 0x50));
    private static readonly SolidColorBrush TodoBrush = new(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush InProgressBrush = new(Color.FromArgb(0xFF, 0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush DoneBrush = new(Color.FromArgb(0xFF, 0x66, 0xBB, 0x6A));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not TaskCardViewModel card)
        {
            return DefaultBrush;
        }

        if (card.IsOverdue)
        {
            return OverdueBrush;
        }

        return card.Status switch
        {
            TaskCardStatus.InProgress => InProgressBrush,
            TaskCardStatus.Done => DoneBrush,
            _ => TodoBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}