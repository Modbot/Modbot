using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// A dragged desktop window is put wholly back on the screen when the drag ends: one inside stays, one off any
/// side is brought back, one bigger than the screen lines up with its top left, and the monitor it goes back
/// onto is the one most of it is on.
/// </summary>
public class KeepOnScreenTests
{
    private static readonly ScreenBox Left = new(0, 0, 1920, 1040);
    private static readonly ScreenBox Right = new(1920, 0, 1920, 1040);

    [Fact]
    public void A_window_inside_stays_where_it_is()
        => Assert.Equal((100, 50), KeepOnScreen.Back(new ScreenBox(100, 50, 520, 720), [Left]));

    [Fact]
    public void A_window_off_the_right_comes_back_to_the_right_edge()
        => Assert.Equal((1400, 50), KeepOnScreen.Back(new ScreenBox(1800, 50, 520, 720), [Left]));

    [Fact]
    public void A_window_off_the_bottom_comes_back_to_the_bottom_edge()
        => Assert.Equal((100, 320), KeepOnScreen.Back(new ScreenBox(100, 900, 520, 720), [Left]));

    [Fact]
    public void A_window_off_the_left_comes_back_to_the_left_edge()
        => Assert.Equal((0, 50), KeepOnScreen.Back(new ScreenBox(-400, 50, 520, 720), [Left]));

    [Fact]
    public void A_window_off_the_top_comes_back_to_the_top_edge()
        => Assert.Equal((100, 0), KeepOnScreen.Back(new ScreenBox(100, -300, 520, 720), [Left]));

    [Fact]
    public void A_sliver_still_showing_counts_as_off_screen()
        => Assert.Equal((1400, 50), KeepOnScreen.Back(new ScreenBox(1910, 50, 520, 720), [Left]));

    [Fact]
    public void A_window_wholly_off_the_screen_comes_back()
        => Assert.Equal((1400, 320), KeepOnScreen.Back(new ScreenBox(3000, 2000, 520, 720), [Left]));

    [Fact]
    public void A_window_bigger_than_the_area_lines_up_with_its_top_left()
        => Assert.Equal((0, 0), KeepOnScreen.Back(new ScreenBox(300, 200, 2500, 1200), [Left]));

    [Fact]
    public void A_work_area_that_does_not_start_at_zero_is_the_edge_it_snaps_to()
        => Assert.Equal((100, 40), KeepOnScreen.Back(new ScreenBox(100, 0, 520, 720), [new ScreenBox(0, 40, 1920, 1000)]));

    [Fact]
    public void The_monitor_with_most_of_the_window_on_it_is_the_one_it_goes_back_onto()
    {
        // 500 of 520 across is on the right monitor, 20 on the left.
        Assert.Equal((1920, 50), KeepOnScreen.Back(new ScreenBox(1900, 50, 520, 720), [Left, Right]));

        // 420 on the left monitor, 100 on the right.
        Assert.Equal((1400, 50), KeepOnScreen.Back(new ScreenBox(1500, 50, 520, 720), [Left, Right]));
    }

    [Fact]
    public void A_window_on_no_monitor_goes_to_the_nearest_one()
        => Assert.Equal((1920, 0), KeepOnScreen.Back(new ScreenBox(1700, -2000, 520, 720), [Left, Right]));

    [Fact]
    public void With_no_monitors_it_stays_where_it_is()
        => Assert.Equal((10, 20), KeepOnScreen.Back(new ScreenBox(10, 20, 520, 720), []));

    [Fact]
    public void A_resized_window_that_runs_off_the_bottom_is_moved_back_up()
        => Assert.Equal(new ScreenBox(100, 140, 520, 900), KeepOnScreen.Fit(new ScreenBox(100, 600, 520, 900), [Left]));

    [Fact]
    public void A_resized_window_off_the_right_is_moved_back_left()
        => Assert.Equal(new ScreenBox(1400, 50, 520, 720), KeepOnScreen.Fit(new ScreenBox(1700, 50, 520, 720), [Left]));

    [Fact]
    public void A_resized_window_taller_than_the_free_area_is_cut_to_it_and_lines_up_with_the_top()
        => Assert.Equal(new ScreenBox(100, 0, 520, 1040), KeepOnScreen.Fit(new ScreenBox(100, 300, 520, 1440), [Left]));

    [Fact]
    public void A_resized_window_inside_stays_as_it_is()
        => Assert.Equal(new ScreenBox(100, 50, 520, 720), KeepOnScreen.Fit(new ScreenBox(100, 50, 520, 720), [Left]));

    [Fact]
    public void A_resized_window_with_no_monitors_stays_as_it_is()
        => Assert.Equal(new ScreenBox(10, 20, 520, 720), KeepOnScreen.Fit(new ScreenBox(10, 20, 520, 720), []));
}
