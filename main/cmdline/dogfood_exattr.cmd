OpenCover.Console.exe -register:user -target:..\..\..\main\packages\NUnit.Runners.2.6.4\tools\nunit-console-x86.exe -targetargs:"OpenCover.Test.dll /noshadow /exclude:AdminOnly" -excludebyattribute:*.ExcludeFromCoverageAttribute -filter:"+[Open*]* -[Open*]*Boot* -[OpenCover.T*]*" -output:opencovertests.xml
