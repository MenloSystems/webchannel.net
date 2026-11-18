# WebChannel.NET

## What is WebChannel.NET?

WebChannel.NET is an implementation of [Qt's WebChannel](https://doc.qt.io/qt-5/qtwebchannel-index.html) protocol in C#/.NET.

This library only depends on [Newtonsoft's Json.NET library](https://www.newtonsoft.com/json) and .NET Standard 2.0.


## Building

Install the .NET Core SDK, then simply run

```
dotnet build
```


## Testing

The tests in `tests` use xUnit v3 and need the .NET SDK 8 or newer. They are
built as a program, so run them with

```
dotnet run --project tests
```
