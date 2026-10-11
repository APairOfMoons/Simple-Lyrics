namespace SimpleLyrics.Core.Editing;

/// <summary>
/// 改动历史：撤销与重做。
/// </summary>
/// <remarks>
/// <para>
/// 两个栈：<c>_done</c> 是已经落下去的，<c>_undone</c> 是撤销回来的。撤销就是把栈顶从
/// 一边搬到另一边并反向执行一次——不需要保存「整个文档的快照」，因为每条命令自己记着
/// 它改了哪几行的前后值。
/// </para>
/// <para>
/// <b>任何新改动都会清空重做栈。</b>这是撤销的通用约定：从历史中间分出一条新支之后，
/// 原来那条支路上的「重做」已经不可能再接上了，留着只会让人以为还能倒回去。
/// </para>
/// <para>
/// <see cref="Position"/> 是「已经落下去的改动条数」。它与<b>保存</b>配成一对：
/// 保存时记下当时的位置，之后只要位置不同就说明有未保存的改动。这样「改一笔又撤回来」
/// 不会留下一个假的「未保存」标记——而按布尔标志位做的话，撤销回去它也不知道该清掉。
/// </para>
/// <para>
/// 刻意<b>不设容量上限</b>：一条命令只记几十个 <c>TimeSpan</c>，一次打轴几百条也不过几万字节，
/// 比起为它设计一个「撤到第 N 步就不许再撤」的规则，直接留着更简单也更少意外。
/// 真到了需要封顶的那天（比如逐字歌词），再按条数或字节数裁剪也不迟。
/// </para>
/// </remarks>
public sealed class TimelineEditHistory
{
    private readonly List<IEditCommand> _done = [];
    private readonly List<IEditCommand> _undone = [];

    /// <summary>已经落下去的改动条数；与保存时的位置比较即可判断有没有未保存的改动。</summary>
    public int Position => _done.Count;

    public int UndoCount => _done.Count;

    public int RedoCount => _undone.Count;

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    /// <summary>
    /// 执行并记下一条改动。
    /// </summary>
    /// <remarks>
    /// 执行这一步在这里做，而不是让调用方先改数据再送进来：那样一来「忘了先执行」
    /// 与「执行了两次」都会静默地留下不一致的历史，而两者都不容易当场发现。
    /// </remarks>
    public void Push(IEditCommand command)
    {
        command.Apply();

        _done.Add(command);
        _undone.Clear();
    }

    /// <summary>撤销一步，返回它的说明；没有可撤销的返回 <c>null</c>。</summary>
    public string? Undo()
    {
        if (_done.Count == 0) return null;

        var command = _done[^1];
        _done.RemoveAt(_done.Count - 1);

        command.Revert();
        _undone.Add(command);

        return command.Label;
    }

    /// <summary>重做一步，返回它的说明；没有可重做的返回 <c>null</c>。</summary>
    public string? Redo()
    {
        if (_undone.Count == 0) return null;

        var command = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);

        command.Apply();
        _done.Add(command);

        return command.Label;
    }

    /// <summary>丢弃全部历史。换了一份歌词（或换了一首曲子）之后必须调用。</summary>
    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
    }
}
