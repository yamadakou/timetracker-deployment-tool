# WinUI.TableView への不具合の報告（下書き）

- 関連: [`desktop-tables.md`](desktop-tables.md) の 10.3.1・REVIEW_ISSUES No.78
- 状況: 下書き（2026-10-29）。オーナーが [w-ahmad/WinUI.TableView の Issue](https://github.com/w-ahmad/WinUI.TableView/issues/new?template=bug_report.md) に投稿する（このセッションには投稿の権限が無い）。作者の貢献の手引き（CONTRIBUTING.md）は「プルリクエストの前に Issue で相談する」ため、まず Issue に修正案の差分を添え、作者が良ければプルリクエストを出す
- 確かめたソース: v1.5.1 と、main の fc181b9（2026-10-23）。どちらにも同じ不具合がある

以下を、そのまま Issue の本文に貼る（英語）。

---

**Title:** `ArgumentOutOfRangeException` in `ScrollRowIntoView` when items are removed right after a single-row selection

### Description:
When exactly one row is selected, `TableView_SelectionChanged` captures the row's index and queues `ScrollRowIntoView(index)` with `DispatcherQueue.TryEnqueue`. If the items shrink before the queued action runs (for example, the selected item is removed from the `ItemsSource`), `ScrollRowIntoView` reads `Items[index]` with a stale index and throws `ArgumentOutOfRangeException`. Because it happens in the queued action, it surfaces as an unhandled exception and terminates the app.

`ScrollRowIntoView` only checks `index < 0`, not `index >= Items.Count`.

The same pattern exists in `SelectRows` (the path taken when `IsReadOnly` is `true`): `DispatcherQueue.TryEnqueue(async () => { var row = await ScrollRowIntoView(slot.Row); ... })`.

Stack trace (v1.5.1, Windows App SDK 2.5.1):
```
System.ArgumentOutOfRangeException: Specified argument was out of the range of valid values. (Parameter 'index')
   at ABI.System.Collections.Generic.IListMethods`1.GetAtHelper(IObjectReference obj, UInt32 index)
   at ABI.System.Collections.Generic.IListMethods`1.Indexer_Get(IObjectReference obj, Int32 index)
   at Microsoft.UI.Xaml.Controls.ItemCollection.get_ListItem(Int32 index)
   at WinUI.TableView.TableView.ScrollRowIntoView(Int32 index)
   at WinUI.TableView.TableView.<>c__DisplayClass40_0.<<TableView_SelectionChanged>b__2>d.MoveNext()
--- End of stack trace from previous location ---
   at System.Threading.Tasks.Task.<>c.<ThrowAsync>b__124_0(Object state)
   at Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext.<>c__DisplayClass2_0.<Post>b__0()
```

### Steps to Reproduce:
1. Bind `ItemsSource` to an `ObservableCollection<T>` with a few items (`SelectionMode="Multiple"` or `Single`).
2. In one synchronous block on the UI thread: select exactly one item (for example, rebuild the collection and re-select the previously selected item), and then remove items so that the collection becomes shorter than the selected index (for example, `Clear()` it).
3. Let the dispatcher run the queued action: `ArgumentOutOfRangeException` is thrown from `ScrollRowIntoView`.

In our app, this happens when the selected row's resource is deleted in the background and the list is rebuilt twice in a row (the first rebuild re-selects the row, the second one removes it).

### Expected behavior:
No exception. If the item is no longer in the list, the queued scroll should do nothing.

### Proposed fix:
Resolve the index when the queued action runs, and make `ScrollRowIntoView` return `null` for an out-of-range index (as it already does for a negative index). This also covers the `slot.Row` path.

```diff
@@ -215,8 +215,9 @@ public partial class TableView : ListView
 
         if (SelectedItems?.Count == 1)
         {
-            var index = Items.IndexOf(SelectedItem);
-            DispatcherQueue.TryEnqueue(async () => await ScrollRowIntoView(index));
+            // Resolve the index when the queued action runs: the items may change (e.g. rows removed) before then.
+            var item = SelectedItem;
+            DispatcherQueue.TryEnqueue(async () => await ScrollRowIntoView(Items.IndexOf(item)));
         }
     }
 
@@ -2627,7 +2628,7 @@ public partial class TableView : ListView
     /// <param name="index">The index of the row to scroll into view.</param>
     public async Task<TableViewRow?> ScrollRowIntoView(int index)
     {
-        if (_scrollViewer is null || index < 0) return default!;
+        if (_scrollViewer is null || index < 0 || index >= Items.Count) return default!;
 
         var item = Items[index];
         index = Items.IndexOf(item); // if the ItemsSource has duplicate items in it. ScrollIntoView will only bring first index of the item.
```

### Screenshots:
N/A

### Environment:
 - Package Version: 1.5.1 (the same code is on `main` at fc181b9)
 - WinAppSDK Version: 2.5.1

### Contribution:
- [x] I am willing to submit a PR to fix this issue
