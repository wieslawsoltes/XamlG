using PackagingSmoke;
using XamlG.Runtime;

var card = new Card();
if (card.Title != "Compiled by the packaged generator" || card.Children.Count != 1)
    throw new InvalidOperationException("The packaged generator did not initialize the view.");
card.ClickGeneratedButton();
if (card.Clicks != 1 || !XamlRuntimeSession.TryGet(card, out var session) || session!.Nodes.Count != 2)
    throw new InvalidOperationException("Generated fields, events, or runtime inspection are incomplete.");
Console.WriteLine("PACKAGING_SMOKE_OK: additional files, generated initializer, fields, event handler and runtime registry.");
