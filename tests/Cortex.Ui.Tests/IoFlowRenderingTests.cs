using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Cortex.Ui.Tests.IoFlowTestApplication))]

namespace Cortex.Ui.Tests;

public sealed class IoFlowTestApplication : Application
{
public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<IoFlowTestApplication>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

        public override void Initialize()
        {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Cortex/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://Cortex/"))
        {
        Source = new Uri("avares://Nodify.Avalonia/Themes/Controls.xaml")
        });
        }
        }

        public class IoFlowRenderingTests
        {
        [AvaloniaFact]
        public void ConnectionTemplate_BindsBothAnchorsAndTracksChanges()
        {
        var view = new IoFlowTabView();
        var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
            var source = new DigitalInputNodeViewModel(new DigitalInput(1, true), default);
            var target = new OutputNodeViewModel(new OutputChannel { ChannelNumber = 1 }, default, [], []);
            var connection = new IoFlowConnectionViewModel(source.OutputConnector, target.InputConnector, "Test");
            source.OutputConnector.Anchor = new Point(100, 80);
            target.InputConnector.Anchor = new Point(500, 180);

            // Exercise the compiled application template, not a test-only replacement.
            Assert.NotNull(editor.ConnectionTemplate);
            var template = Assert.IsType<Grid>(editor.ConnectionTemplate.Build(connection));
                template.DataContext = connection;
                var wires = template.GetVisualDescendants().OfType<Connection>().ToArray();
                var wire = Assert.Single(wires, item => item.StrokeThickness == 4);
                var hitTarget = Assert.Single(wires, item => item.StrokeThickness == 16);
                var glow = Assert.Single(wires, item => item.StrokeThickness == 9);
                Assert.False(glow.IsVisible);
                Assert.Equal(source.OutputConnector.Anchor, wire.Source);
                Assert.Equal(target.InputConnector.Anchor, wire.Target);
                Assert.False(wire.IsHitTestVisible);
                Assert.True(hitTarget.IsHitTestVisible);
                Assert.Equal("#FF8254B8", connection.Stroke);
                connection.IsHovered = true;
                Assert.Equal("#FFB794EB", connection.Stroke);
                Assert.Equal(5, wire.StrokeThickness);
                connection.IsSelected = true;
                Assert.Equal("#FFE0C8FF", connection.Stroke);
                Assert.Equal(7, wire.StrokeThickness);
                Assert.True(glow.IsVisible);
                Assert.False(glow.IsHitTestVisible);
                Assert.Equal(Color.Parse("#99C8A7FF"), Assert.IsAssignableFrom<ISolidColorBrush>(glow.Stroke).Color);
                Assert.Equal(Color.Parse("#FFC8A7FF"), Assert.IsType<DropShadowEffect>(glow.Effect).Color);
                connection.IsSelected = false;
                Assert.False(glow.IsVisible);

                source.OutputConnector.Anchor = new Point(130, 90);
                target.InputConnector.Anchor = new Point(620, 230);
                Assert.Equal(source.OutputConnector.Anchor, wire.Source);
                Assert.Equal(target.InputConnector.Anchor, wire.Target);
                }

                [AvaloniaFact]
                public void Connections_CreateVisibleGeometryAndFollowNodeLayout()
                {
                var graph = new IoFlowGraphViewModel();
                var source = new DigitalInputNodeViewModel(new DigitalInput(1, true), new Point(50, 50));
                var target = new OutputNodeViewModel(new OutputChannel { ChannelNumber = 1 }, new Point(550, 100), [], []);
                graph.Nodes.Add(source);
                graph.Nodes.Add(target);
                source.OutputConnector.IsConnected = true;
                target.InputConnector.IsConnected = true;
                var connection = new IoFlowConnectionViewModel(source.OutputConnector, target.InputConnector, "Test");
                graph.Connections.Add(connection);
                var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
                var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
                    var window = new Window { Width = 1200, Height = 900, Content = view };

                    try
                    {
                    window.Show();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var wire = Assert.Single(view.GetVisualDescendants().OfType<Connection>()
.Where(item => ReferenceEquals(item.DataContext, connection) && item.StrokeThickness < 8));

                            Assert.NotEqual(default, source.OutputConnector.Anchor);
                            Assert.NotEqual(source.OutputConnector.Anchor, target.InputConnector.Anchor);
                            Assert.Equal(source.OutputConnector.Anchor, wire.Source);
                            Assert.Equal(target.InputConnector.Anchor, wire.Target);
                            Assert.True(wire.IsEffectivelyVisible);
Assert.True(wire.Opacity > 0);
                                Assert.NotNull(wire.Stroke);
Assert.True(wire.StrokeThickness > 0);
                                    Assert.NotNull(wire.RenderedGeometry);
Assert.True(wire.RenderedGeometry.Bounds.Width > 0);

                                        Point previousTarget = wire.Target;
                                        target.Location = new Point(650, 200);
                                        window.UpdateLayout();
                                        Dispatcher.UIThread.RunJobs();
                                        Assert.NotEqual(previousTarget, wire.Target);
                                        Assert.Equal(target.InputConnector.Anchor, wire.Target);

                                        // Anchors stay in graph coordinates; the shared viewport transform handles pan/zoom.
                                        Point graphSource = wire.Source;
                                        Point graphTarget = wire.Target;
                                        editor.ViewportZoom = 0.75;
                                        editor.ViewportLocation = new Point(30, 40);
                                        window.UpdateLayout();
                                        Dispatcher.UIThread.RunJobs();
                                        Assert.Equal(graphSource, wire.Source);
                                        Assert.Equal(graphTarget, wire.Target);
                                        var sourceConnector = Assert.Single(view.GetVisualDescendants().OfType<Connector>()
.Where(item => ReferenceEquals(item.DataContext, source.OutputConnector)));
                                                Point? connectorCenter = sourceConnector.TranslatePoint(
                                                new Point(sourceConnector.Bounds.Width / 2, sourceConnector.Bounds.Height / 2), editor);
                                                Point? wireStart = wire.TranslatePoint(wire.Source, editor);
                                                Assert.NotNull(connectorCenter);
                                                Assert.NotNull(wireStart);
                                                Assert.Equal(connectorCenter.Value.X, wireStart.Value.X, 3);
                                                Assert.Equal(connectorCenter.Value.Y, wireStart.Value.Y, 3);

                                                graph.Connections.Clear();
                                                window.UpdateLayout();
                                                Dispatcher.UIThread.RunJobs();
                                                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Connection>(),
item => ReferenceEquals(item.DataContext, connection));

                                                        graph.Connections.Add(connection);
                                                        window.UpdateLayout();
                                                        Dispatcher.UIThread.RunJobs();
                                                        var restoredWire = Assert.Single(view.GetVisualDescendants().OfType<Connection>()
.Where(item => ReferenceEquals(item.DataContext, connection) && item.StrokeThickness < 8));
                                                                Assert.Equal(source.OutputConnector.Anchor, restoredWire.Source);
                                                                Assert.Equal(target.InputConnector.Anchor, restoredWire.Target);
                                                                }
                                                                finally
                                                                {
                                                                window.Close();
                                                                }
                                                                }
                                                                }
