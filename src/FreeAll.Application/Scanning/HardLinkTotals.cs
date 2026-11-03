namespace FreeAll.Application.Scanning;

/// <summary>One name of a hard-linked file: the folder it is in and the sizes that name reports.</summary>
/// <param name="DirectoryKey">Folder key.</param>
/// <param name="LogicalSize">Logical size reported for this name.</param>
/// <param name="AllocatedSize">Allocated size reported for this name.</param>
public readonly record struct LinkName(int DirectoryKey, long LogicalSize, long AllocatedSize);

/// <summary>A correction applied at one folder when totals are rolled up.</summary>
public readonly record struct LinkAdjustment(long LogicalBytes, long AllocatedBytes);

/// <summary>
/// Hard-link-aware folder totals (F-028, TD-026). Folder totals are sums over the names in each subtree,
/// so a file with several names counts several times. For each hard-linked file, its names are put in
/// tree preorder and one copy is subtracted at the lowest common ancestor of every consecutive pair. A
/// subtree holding <c>m</c> of the names then receives exactly <c>m − 1</c> corrections, so after the
/// usual bottom-up roll-up every folder counts the file once. Names of one file normally report the same
/// size; when they don't (NTFS updates each name's copy lazily), the smallest is subtracted, so a total is
/// never reduced below what its largest name reports.
/// </summary>
public sealed class HardLinkTotals
{
    private readonly Dictionary<int, int> _indexByKey;
    private readonly int[] _keys;
    private readonly int[] _parent;
    private readonly int[] _depth;
    private readonly int[] _preorder;
    private readonly Dictionary<int, LinkAdjustment> _adjustments = [];
    private readonly List<LinkName> _scratch = [];

    /// <param name="directories">Every folder of the session: key, parent key (null for the root) and depth.</param>
    public HardLinkTotals(IReadOnlyList<(int Key, int? ParentKey, int Depth)> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var count = directories.Count;
        _indexByKey = new Dictionary<int, int>(count);
        _keys = new int[count];
        _parent = new int[count];
        _depth = new int[count];
        _preorder = new int[count];
        for (var i = 0; i < count; i++)
        {
            _keys[i] = directories[i].Key;
            _depth[i] = directories[i].Depth;
            _indexByKey[directories[i].Key] = i;
        }

        // First-child / next-sibling lists, then an iterative depth-first walk for preorder numbers.
        var firstChild = new int[count];
        var nextSibling = new int[count];
        Array.Fill(firstChild, -1);
        Array.Fill(nextSibling, -1);
        var roots = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var parentKey = directories[i].ParentKey;
            if (parentKey is { } key && _indexByKey.TryGetValue(key, out var parent))
            {
                _parent[i] = parent;
                nextSibling[i] = firstChild[parent];
                firstChild[parent] = i;
            }
            else
            {
                _parent[i] = -1;
                roots.Add(i);
            }
        }

        var order = 0;
        var stack = new Stack<int>();
        foreach (var root in roots)
        {
            stack.Push(root);
            while (stack.TryPop(out var node))
            {
                _preorder[node] = order++;
                for (var child = firstChild[node]; child >= 0; child = nextSibling[child])
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>Extra names counted (a file with three names adds two).</summary>
    public long ExtraNames { get; private set; }

    /// <summary>Logical bytes subtracted in total, which is what the scan root's total drops by.</summary>
    public long LogicalBytesRemoved { get; private set; }

    public long AllocatedBytesRemoved { get; private set; }

    /// <summary>Corrections by folder key (all values are zero or negative).</summary>
    public IReadOnlyDictionary<int, LinkAdjustment> Adjustments => _adjustments;

    /// <summary>Adds one hard-linked file. Names in folders that are not in the tree are ignored.</summary>
    /// <returns>The size subtracted per extra name, or null when fewer than two names are in the tree.</returns>
    public LinkAdjustment? AddFile(IEnumerable<LinkName> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        _scratch.Clear();
        foreach (var name in names)
        {
            if (_indexByKey.ContainsKey(name.DirectoryKey))
            {
                _scratch.Add(name);
            }
        }

        if (_scratch.Count < 2)
        {
            return null;
        }

        var logical = long.MaxValue;
        var allocated = long.MaxValue;
        foreach (var name in _scratch)
        {
            logical = Math.Min(logical, name.LogicalSize);
            allocated = Math.Min(allocated, name.AllocatedSize);
        }

        _scratch.Sort((a, b) => _preorder[_indexByKey[a.DirectoryKey]].CompareTo(_preorder[_indexByKey[b.DirectoryKey]]));
        for (var i = 1; i < _scratch.Count; i++)
        {
            var ancestor = CommonAncestor(_indexByKey[_scratch[i - 1].DirectoryKey], _indexByKey[_scratch[i].DirectoryKey]);
            if (ancestor < 0)
            {
                continue; // Separate roots: nothing above both, so nothing double counts them.
            }

            var key = _keys[ancestor];
            var current = _adjustments.GetValueOrDefault(key);
            _adjustments[key] = new LinkAdjustment(current.LogicalBytes - logical, current.AllocatedBytes - allocated);
            ExtraNames++;
            LogicalBytesRemoved += logical;
            AllocatedBytesRemoved += allocated;
        }

        return new LinkAdjustment(logical, allocated);
    }

    private int CommonAncestor(int a, int b)
    {
        while (a >= 0 && b >= 0 && a != b)
        {
            if (_depth[a] >= _depth[b])
            {
                a = _parent[a];
            }
            else
            {
                b = _parent[b];
            }
        }

        return a == b ? a : -1;
    }
}
