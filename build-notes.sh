#!/bin/bash

#sudo apt-get install -y dotnet-sdk-9.0
dotnet restore Jellyfin.Plugin.Dlna.sln
dotnet build Jellyfin.Plugin.Dlna.sln -c Release
dotnet publish Jellyfin.Plugin.Dlna.sln -c Release -o ./publish
