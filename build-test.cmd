@echo off
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:exe -out:annotate-geom-test.exe -r:System.dll -r:System.Drawing.dll shot-annotate-geom.cs shot-annotate-test.cs
