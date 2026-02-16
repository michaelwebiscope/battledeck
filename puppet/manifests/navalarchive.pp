# Naval Archive - Puppet manifest (no extra modules required)
# Apply: puppet apply manifests/navalarchive.pp
# Or: puppet apply manifests/site.pp

class navalarchive {

  # Create app directories
  exec { 'create-api-dir':
    command  => 'New-Item -ItemType Directory -Force -Path C:\inetpub\navalarchive-api | Out-Null',
    creates  => 'C:/inetpub/navalarchive-api',
    provider => powershell,
  }

  exec { 'create-web-dir':
    command  => 'New-Item -ItemType Directory -Force -Path C:\inetpub\navalarchive-web | Out-Null',
    creates  => 'C:/inetpub/navalarchive-web',
    provider => powershell,
  }

  # Install Chocolatey
  exec { 'install-chocolatey':
    command  => @(EOT)
      Set-ExecutionPolicy Bypass -Scope Process -Force
      [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
      iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
      | EOT
    creates  => 'C:/ProgramData/chocolatey/bin/choco.exe',
    provider => powershell,
  }

  # Install .NET 8
  exec { 'install-dotnet8':
    command  => 'choco install dotnet-8.0 -y',
    unless   => 'dotnet --list-runtimes | Select-String "Microsoft.AspNetCore.App 8.0"',
    provider => powershell,
    require  => Exec['install-chocolatey'],
  }

  # Install Node.js LTS
  exec { 'install-nodejs':
    command  => 'choco install nodejs-lts -y',
    unless   => 'node --version 2>$null',
    provider => powershell,
    require  => Exec['install-chocolatey'],
  }

  # Enable IIS
  exec { 'enable-iis':
    command  => 'Install-WindowsFeature -Name Web-Server, Web-Asp-Net45, Web-WebSockets -IncludeManagementTools',
    unless   => 'Get-WindowsFeature -Name Web-Server | Where-Object {$_.Installed}',
    provider => powershell,
  }

  # Install URL Rewrite
  exec { 'install-url-rewrite':
    command  => @(EOT)
      $url = "https://download.microsoft.com/download/1/2/8/128E2E22-1AE5-47A7-AC89-B6F4705211A3/rewrite_amd64_en-US.msi"
      $out = "$env:TEMP\urlrewrite.msi"
      Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
      Start-Process msiexec.exe -ArgumentList "/i", $out, "/quiet" -Wait
      | EOT
    creates  => 'C:/Program Files/IIS/URL Rewrite/rewrite.dll',
    provider => powershell,
  }

  # API web.config
  file { 'C:/inetpub/navalarchive-api/web.config':
    ensure  => file,
    content => @(EOT)
      <?xml version="1.0" encoding="utf-8"?>
      <configuration>
        <location path="." inheritInChildApplications="false">
          <system.webServer>
            <handlers>
              <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
            </handlers>
            <aspNetCore processPath="dotnet" arguments=".\NavalArchive.Api.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
          </system.webServer>
        </location>
      </configuration>
      | EOT
    require => Exec['create-api-dir'],
  }

  # Ensure Application Request Routing for proxy (optional - install if needed)
  exec { 'iisreset':
    command  => 'iisreset',
    provider => powershell,
    require  => [Exec['enable-iis'], Exec['install-url-rewrite']],
  }
}

include navalarchive
