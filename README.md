# azftp2blob
FTP to Azure Blob Storage Proxy

## Build and run
.NET 8:

```
dotnet build azftp2blob.sln
```

Configuration is in `appsettings.json` next to `azftp2blob.exe` (keys as in the former `azftp2blob.exe.config`).
FTP accounts are in `userlist.txt` (`user;password;container[;rights]`, not in git). `userlist.txt` and a relative
`LogPath` are resolved against the working directory, so start the server from its folder.

## Tests
`AzureFileSystemTests` run against [Azurite](https://github.com/Azure/Azurite) and are skipped when it's not running:

```
npx --package azurite azurite-blob --skipApiVersionCheck --loose
dotnet test
```
