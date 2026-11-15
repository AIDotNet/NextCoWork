using System.Text.Json.Nodes;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Agent.Runs.Tests.Support;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Runs.Tests;

/// <summary>纯函数:下一批发哪些、怎么合并、存档形状往返。移植自 2.x <c>queued-input.test.ts</c>(pickNextBatch / mergeBatch / batchToParts / 序列化往返 / isLive 的校验)。</summary>
public sealed class QueueBatchTests
{
    private static QueueItem Item(string id, string text, QueueState state = QueueState.Pending, long order = 0, long enqueued = 0, params Part[] extra) =>
        new(new MessageId(id), [new TextPart(text), .. extra], state, order, enqueued, state == QueueState.Promoted ? enqueued : null, Opts.Default);

    [Fact]
    public void Without_promoted_items_it_falls_back_to_fifo_and_takes_one()
    {
        var picked = QueueBatches.PickNext([Item("a", "A"), Item("b", "B")]);
        Assert.Equal(["a"], picked.Select(i => i.Id.Value));
    }

    [Fact]
    public void An_empty_queue_picks_nothing()
    {
        Assert.Empty(QueueBatches.PickNext([]));
    }

    [Fact]
    public void With_promoted_items_only_those_go_not_one_pending_alongside()
    {
        var picked = QueueBatches.PickNext([Item("a", "A"), Item("b", "B", QueueState.Promoted, 1), Item("c", "C")]);
        Assert.Equal(["b"], picked.Select(i => i.Id.Value));
    }

    [Fact]
    public void Several_promoted_items_go_in_promotion_order_not_queue_order()
    {
        var picked = QueueBatches.PickNext([Item("a", "A", QueueState.Promoted, 2), Item("b", "B", QueueState.Promoted, 1)]);
        Assert.Equal(["b", "a"], picked.Select(i => i.Id.Value));
    }

    [Fact]
    public void Merge_joins_with_a_blank_line_and_skips_whitespace_only_items()
    {
        var merged = QueueBatches.Merge([Item("a", "one"), Item("b", "  \n "), Item("c", "two ")]);
        Assert.Equal("one\n\ntwo", ((TextPart)Assert.Single(merged.Parts)).Text);
        Assert.Empty(merged.DeferredIds);
    }

    [Fact]
    public void Merge_dedupes_attachments_by_reference()
    {
        var image = new ImagePart("image/png", "ncw://attachments/x.png");
        var merged = QueueBatches.Merge([Item("a", "A", extra: image), Item("b", "B", extra: image)]);
        Assert.Single(merged.Parts.OfType<ImagePart>());
    }

    [Fact]
    public void Over_the_limit_the_rest_is_returned_not_truncated_and_the_first_item_always_goes_whole()
    {
        var big = new string('x', QueueBatches.MaxText + 5);
        var merged = QueueBatches.Merge([Item("a", big), Item("b", "tiny"), Item("c", "also tiny")]);
        Assert.Equal(big, ((TextPart)merged.Parts[0]).Text);
        Assert.Equal(["b", "c"], merged.DeferredIds.Select(i => i.Value)); // 剩下的全部退回,不再尝试塞小的(保持顺序)
    }

    [Fact]
    public void An_empty_batch_merges_to_no_parts()
    {
        Assert.Empty(QueueBatches.Merge([]).Parts);
    }

    // ── 存档形状 ──

    [Fact]
    public void An_item_survives_the_storage_round_trip_with_text_attachments_and_options()
    {
        var options = new SendOptions(new ModelRef("prov", "alias"), ThinkingLevel.High, true, "plan", "fullAccess");
        var item = new QueueItem(new MessageId("m1"), [new TextPart("hello"), new ImagePart("image/jpeg", "ncw://attachments/a.jpg"), new FileRefPart("src/a.cs", "a.cs")], QueueState.Promoted, 3, 100, 150, options);
        var back = QueueCodec.FromStorage(QueueCodec.ToStorage(item))!;
        Assert.Equal(item.Id, back.Id);
        Assert.Equal(options, back.Options);
        Assert.Equal(QueueState.Promoted, back.State);
        Assert.Equal(150, back.PromotedAt);
        Assert.Equal("hello", ((TextPart)back.Parts[0]).Text);
        Assert.Equal(new ImagePart("image/jpeg", "ncw://attachments/a.jpg"), back.Parts[1]);
        Assert.Equal(new FileRefPart("src/a.cs", "a.cs"), back.Parts[2]);
    }

    [Fact]
    public void A_2x_row_with_extra_option_keys_is_read_leniently_and_a_row_in_a_terminal_state_is_not_read_at_all()
    {
        var row = new Ncw.Agent.Storage.Sessions.QueuedInput
        {
            Id = "m1", Text = "hi", Attachments = [], Status = "pending", EnqueuedAt = 5,
            Options = new JsonObject { ["model"] = "deepseek", ["thinking"] = "auto", ["workspaceId"] = "w", ["planExecution"] = true },
        };
        var item = QueueCodec.FromStorage(row)!;
        Assert.Equal("deepseek", item.Options.Model.Alias);
        Assert.Equal(ThinkingLevel.Auto, item.Options.Thinking);

        row.Status = "consumed";
        Assert.Null(QueueCodec.FromStorage(row));
        row.Status = "pending";
        row.Id = "";
        Assert.Null(QueueCodec.FromStorage(row));
    }

    [Fact]
    public void Only_pending_and_promoted_are_ever_written()
    {
        var pending = QueueCodec.ToStorage(Item("a", "A"));
        var promoted = QueueCodec.ToStorage(Item("b", "B", QueueState.Promoted, 1, 7));
        Assert.Equal("pending", pending.Status);
        Assert.Null(pending.PromotedAt);
        Assert.Equal("promoted", promoted.Status);
        Assert.Equal(7, promoted.PromotedAt);
    }
}
