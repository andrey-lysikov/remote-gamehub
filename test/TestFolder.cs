//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.Tests;

// A folder of its own for one test, under the temporary directory, removed when the test is done.
// A test that leaves files behind is a test that passes differently the second time.
internal sealed class TestFolder : IDisposable
{
    internal string Path { get; }

    internal TestFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Remote-Gamehub-tests",
                                      Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    // A file in this folder, with the given text, its parent folders created as needed.
    internal string File(string relative, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // A handle still open on Windows keeps the folder for a moment; the next test uses
            // a folder of its own anyway.
        }
    }
}
