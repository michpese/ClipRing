using System.Collections.ObjectModel;
using ClipRing.Core;

namespace ClipRing.UI;

/// <summary>An option whose label can change without the ComboBox losing its selection.</summary>
public sealed class LiveOption<T> : ObservableObject
{
    public LiveOption(T value, string label)
    {
        Value = value;
        _label = label;
    }

    public T Value { get; }

    private string _label;
    public string Label { get => _label; set => Set(ref _label, value); }
}

/// <summary>
/// Options for a ComboBox whose labels are refined later (monitors after detection, encoders after the
/// FFmpeg check). Replacing the whole list makes WPF drop the selection as soon as the selected entry's label
/// changes, because it compares the entries, not their values: the box then shows nothing although the
/// setting is still there. Updating in place keeps the selected entry and only changes its text.
/// </summary>
public sealed class LiveOptions<T> : ObservableCollection<LiveOption<T>>
{
    public void Update(IEnumerable<(T Value, string Label)> wanted)
    {
        var list = wanted.ToList();
        var same = EqualityComparer<T>.Default;

        for (int i = Count - 1; i >= 0; i--)
        {
            if (!list.Any(w => same.Equals(w.Value, this[i].Value))) RemoveAt(i);
        }

        for (int i = 0; i < list.Count; i++)
        {
            var (value, label) = list[i];
            int at = -1;
            for (int j = 0; j < Count; j++)
            {
                if (same.Equals(this[j].Value, value)) { at = j; break; }
            }

            if (at < 0)
            {
                Insert(i, new LiveOption<T>(value, label));
                continue;
            }
            if (at != i) Move(at, i);
            this[i].Label = label;
        }
    }
}
