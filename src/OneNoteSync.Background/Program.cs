// onenote-sync-background.exe: no window, ever. What the logon task runs (`watch`); anything else runs as the
// console program would, with nothing shown.
return OneNoteSync.Cli.Run(args, new OneNoteSync.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
