---
name: contoso.widgets-widget-usage
description: Correct usage patterns for the Contoso.Widgets library, including lifetime rules and the batching API. Use whenever code creates, configures, or disposes a Widget.
---

# Using Contoso.Widgets

Create widgets through `WidgetFactory`, never with `new Widget()` directly — the
factory is what registers the instance with the batching pipeline.

See `references/batching.md` for the batching rules.
