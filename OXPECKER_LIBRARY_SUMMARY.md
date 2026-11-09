# Oxpecker Library Summary

## What Oxpecker is

Oxpecker is an F# web framework built on top of ASP.NET Core endpoint routing. It is deliberately shaped like a functional, composable alternative to minimal APIs, but with a stronger emphasis on F# ergonomics, pipeline composition, and typed request handling.

The project description in this repository frames it as a refined version of Giraffe: same general philosophy, cleaner core abstractions, better runtime characteristics, and a more modern ASP.NET Core integration model. In practice, it combines the idiomatic request/response model of functional web frameworks with the ecosystem and runtime of ASP.NET Core.

At a high level, Oxpecker is a library for:

- defining endpoints as functions rather than classes
- composing request pipelines using small, focused handlers and middleware
- binding HTTP data into typed F# models
- rendering HTML, JSON, or streaming responses
- integrating with ASP.NET Core features without losing the F# functional style

## The core mental model

The main building blocks are the two core types defined in `src/Oxpecker/Core.fs`:

```fsharp
type EndpointHandler = HttpContext -> Task
type EndpointMiddleware = EndpointHandler -> EndpointHandler
```

This is the conceptual heart of the framework.

### EndpointHandler

An `EndpointHandler` is the terminal unit of request processing: it receives an `HttpContext` and produces a `Task`. It is roughly the functional equivalent of an ASP.NET Core `RequestDelegate`, but expressed in an F#-friendly shape. Typical handlers do things like:

- read request data
- validate it
- write a response body
- set headers, status codes, or redirects

The built-in handlers in `src/Oxpecker/Handlers.fs` show this clearly. For example:

- `text` writes plain text
- `json` writes JSON
- `htmlView` renders a view
- `redirectTo` redirects the client
- `setStatusCode`, `setHttpHeader`, and `setContentType` manipulate the response
- `bindJson`, `bindForm`, and `bindQuery` bridge direct request payloads into typed models

### EndpointMiddleware

An `EndpointMiddleware` is a function that takes the next handler and returns a new handler. This lets code intercept, decorate, transform, or short-circuit the request pipeline before or after the next step.

This is where Oxpecker gets its expressive power: middleware can:

- inspect the request before it reaches the route logic
- insert logging, auth, or validation behavior
- change response headers or status codes
- decide to continue or stop processing

The `>=>` operator composes handlers and middleware into a single pipeline. The implementation in `Core.fs` is simple but important: it respects `HttpResponse.HasStarted`, so once a response begins, later components in the pipeline do not run unnecessarily.

```fsharp
let app =
    route "/" (
        setHttpHeader "X-Foo" "Bar"
        >=> setStatusCode 200
        >=> text "Hello World"
    )
```

This is a classic functional pipeline model: small units combine into larger endpoint behavior, and each stage can short-circuit or pass through.

## Composition and the fish operator

The most recognizable part of the library is the `>=>` composition operator. Oxpecker supports composing:

- `EndpointMiddleware` with `EndpointMiddleware`
- `EndpointMiddleware` with `EndpointHandler`
- `EndpointHandler` with `EndpointHandler`

This makes it easy to build web applications in a way that is readable and modular without object-heavy controller patterns.

The library also provides ergonomics such as:

- `>>=>` for handlers with one extra argument
- `>>=>+` for two arguments
- `>>=>++` for three arguments

These are especially useful when combined with route helpers like `routef` and binding helpers like `bindJson`, `bindForm`, and `bindQuery`.

The result is a functional web app that reads like a pipeline graph rather than a big imperative `Program.cs` file.

## Routing in the core library

The routing system is one of the clearest examples of Oxpecker’s design. The route support in `src/Oxpecker/Routing.fs` sits directly on ASP.NET Core endpoint routing while preserving an F#-oriented DSL.

The framework defines route metadata such as:

- `HttpVerb` values (`GET`, `POST`, etc.)
- `HttpVerbs` unions and route templates
- `Endpoint` variants for simple, nested, and multi-endpoint structures

The route parser does a few notable things:

- parses route placeholders like `{%s}`, `{%i}`, `{%b}`, `{%f}`, `{%O:guid}`
- converts them into ASP.NET Core endpoint route templates
- maps route segments to strongly typed parameters via reflection
- caches reflection-driven routing metadata to avoid repeated work

The `RouteTemplateBuilder` and `routefInfoCache` logic is a subtle but important part of the library: Oxpecker reflects over handler signatures to infer which route parameters should be provided, then binds the values to the function arguments. That is one reason it feels ergonomic in F# while still operating on ASP.NET Core’s underlying routing engine.

### Why routef matters

The `routef` pattern in Oxpecker allows a route path and a handler to be linked by route placeholders. It can infer types from values like:

- string (`%s`)
- int (`%i`)
- bool (`%b`)
- float (`%f`)
- Guid (`%O:guid`)

This means handlers can be written mostly as normal F# functions, with route variables injected neatly into the function call.

## Middleware and integration with ASP.NET Core

Oxpecker is not a replacement for ASP.NET Core; it is an integration layer built on top of it.

In `src/Oxpecker/Middleware.fs`, the library exposes:

```fsharp
UseOxpecker(builder, endpoints)
UseOxpecker(builder, endpoint)
```

It also adds default services through `AddOxpecker`, including:

- `IJsonSerializer`
- `IModelBinder`
- an ASP.NET Core logger

This is a very important design decision: Oxpecker leans on ASP.NET Core’s infrastructure instead of reimplementing the entire web stack. It adds a compositional F# layer, not a totally separate runtime.

The default exception middleware in `Handlers.fs` handles a useful set of cases:

- client disconnect / request timeout cancellation
- invalid request data (`ModelBindException`, `RouteParseException`)
- antiforgery failure
- generic unhandled exceptions

This gives Oxpecker a strong “safe default” behavior without forcing the user to build everything from scratch.

## Model binding and request parsing

One of the strongest parts of the library is typed request binding.

`src/Oxpecker/ModelBinder.fs` defines:

```fsharp
type IModelBinder =
    abstract member Bind<'T> : seq<KeyValuePair<string, StringValues>> -> 'T
```

This model binder supports binding from:

- query strings
- form data
- JSON input
- complex nested payloads
- arrays and indexed collections

The implementation is notably careful about malformed input. It checks collection sizes, enforces a maximum collection bound (defaulting to 1024), and avoids unsafe parsing behavior. This is not just convenience code: it is request parsing logic tuned for robustness.

The library also uses TypeShape-based type inspection to support `IParsable`-compatible values and a broad set of POCO binding scenarios. That enables concise user APIs like `ctx.BindJson<MyType>()` while still handling common ASP.NET Core form/query patterns.

This is a big part of the framework’s practical value: it makes typed server-side development feel natural while still being realistic for real HTTP traffic.

## Response writing and serialization

The default built-in endpoint handlers cover a wide spectrum of normal web application responses.

Key patterns include:

- `text` for plain text
- `bytes` for binary responses
- `json` / `jsonChunked` for JSON payloads
- `htmlView` / `htmlChunked` for HTML rendering
- `redirectTo` for redirects
- `setStatusCode` / `setHttpHeader` / `setContentType` for low-level response control

The library is written to make response writing feel like ordinary F# composition rather than imperative ASP.NET Core code. The `~%` operator defined in `Core.fs` even allows an `IResult` to be treated as an `EndpointHandler`, showing how Oxpecker integrates with ASP.NET Core’s own `IResult` abstractions.

## The “Oxpecker” style of development

The project’s design philosophy is very clear from the README and source:

- prefer small functional building blocks over big object graphs
- keep request and response work explicit and composable
- use ASP.NET Core as the runtime substrate rather than reinventing it
- lean into F#’s pipeline and composition strengths

This is one of the reasons the library appeals to developers who like the “minimal API but functional” experience. It gives F# users a more expressive and compositional model than raw ASP.NET Core endpoint registration while still staying close to the underlying platform.

## Companion packages and ecosystem

The monorepo includes several related packages, each extending the same philosophy in different areas:

- `Oxpecker.ViewEngine`: type-safe HTML generation and template DSL
- `Oxpecker.Htmx`: HTMX helpers and attributes
- `Oxpecker.Alpine`: Alpine.js integration
- `Oxpecker.OpenApi`: OpenAPI and route metadata generation
- `Oxpecker.Solid`: Solid.js-oriented frontend rendering and bindings
- `Oxpecker.Solid.FablePlugin`: plugin support for Fable/Solid interop

This is a strong signal that the library is not just about server-side routing; it is designed as a broader full-stack F# stack that integrates modern frontend approaches without abandoning backend discipline.

## How it fits in the wider ecosystem

Oxpecker sits in a space between:

- ASP.NET Core Minimal APIs
- Giraffe-style functional web programming
- server-rendered web apps
- full-stack F# solutions with frontend components

It is strongest when you want:

- idiomatic F# endpoints
- compositional middleware
- typed request models
- HTML or API responses built without ceremony
- integration with HTMX or Solid.js in a single language ecosystem

It is less of a “framework that replaces everything” and more of a “functional layer on top of ASP.NET Core” that feels natural to F# developers and can scale to rich app designs without turning into controller-heavy code.

## Architectural takeaways

If you distill the core library to its essence, Oxpecker is built around five ideas:

1. Function-first HTTP handlers
2. Composable middleware pipelines
3. Typed route and model binding from real HTTP requests
4. Strong ASP.NET Core interoperability
5. A full-stack F# story beyond the backend core

That combination is what gives the project its identity: it is not merely a wrapper around ASP.NET Core; it is a deliberate functional web programming model that keeps ASP.NET Core as the execution engine while expressing application behavior in a more F#-native way.

## Bottom line

Oxpecker is best understood as a high-performance, F#-oriented web framework for ASP.NET Core that emphasizes:

- endpoint composition
- typed data binding
- functional request flow
- server-side productivity
- integration with HTML and modern frontend patterns

Its core library is elegant and compact, but the surrounding ecosystem shows that the project aims for more than a simple routing helper: it is a real full-stack framework for F# developers who want to build modern web apps without leaving the functional programming model.
