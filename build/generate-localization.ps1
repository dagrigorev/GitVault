#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates the three .resx resource files and the strongly-typed Keys class from
    build/loc/strings.json.

.DESCRIPTION
    strings.json is the single source of truth for every user-visible string. Generating
    all three cultures from one file makes it structurally impossible for the key sets to
    drift apart, which is what the localization parity test asserts.

    Run this after editing strings.json, then commit the generated files.
#>
[CmdletBinding()]
param(
    [string] $SourceFile = (Join-Path $PSScriptRoot 'loc/strings.json'),
    [string] $ResourceDirectory = (Join-Path $PSScriptRoot '../src/GitVault.Localization/Resources'),
    [string] $KeysFile = (Join-Path $PSScriptRoot '../src/GitVault.Localization/Keys.g.cs')
)

$ErrorActionPreference = 'Stop'

$entries = Get-Content -LiteralPath $SourceFile -Raw -Encoding utf8 | ConvertFrom-Json

$duplicates = $entries | Group-Object -Property key | Where-Object { $_.Count -gt 1 }
if ($duplicates) {
    throw "Duplicate keys in ${SourceFile}: $($duplicates.Name -join ', ')"
}

foreach ($entry in $entries) {
    foreach ($lang in @('en', 'ru', 'zh')) {
        if ([string]::IsNullOrWhiteSpace($entry.$lang)) {
            throw "Key '$($entry.key)' has no '$lang' translation."
        }
    }
}

$header = @'
<?xml version="1.0" encoding="utf-8"?>
<root>
  <!--
    GENERATED FILE - do not edit by hand.
    Source: build/loc/strings.json    Generator: build/generate-localization.ps1
  -->
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
'@

function Write-Resx {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Language
    )

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine($header)
    foreach ($entry in $entries) {
        $name = [System.Security.SecurityElement]::Escape($entry.key)
        $value = [System.Security.SecurityElement]::Escape($entry.$Language)
        [void]$builder.AppendLine("  <data name=`"$name`" xml:space=`"preserve`">")
        [void]$builder.AppendLine("    <value>$value</value>")
        [void]$builder.AppendLine('  </data>')
    }
    [void]$builder.AppendLine('</root>')

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $builder.ToString(), $utf8NoBom)
    Write-Host "wrote $Path ($($entries.Count) keys)"
}

New-Item -ItemType Directory -Force -Path $ResourceDirectory | Out-Null

Write-Resx -Path (Join-Path $ResourceDirectory 'Strings.resx')         -Language 'en'
Write-Resx -Path (Join-Path $ResourceDirectory 'Strings.ru.resx')      -Language 'ru'
Write-Resx -Path (Join-Path $ResourceDirectory 'Strings.zh-Hans.resx') -Language 'zh'

$keys = [System.Text.StringBuilder]::new()
[void]$keys.AppendLine('// <auto-generated />')
[void]$keys.AppendLine('// Generated from build/loc/strings.json by build/generate-localization.ps1. Do not edit.')
[void]$keys.AppendLine()
[void]$keys.AppendLine('namespace GitVault.Localization;')
[void]$keys.AppendLine()
[void]$keys.AppendLine('/// <summary>Every resource key defined by GitVault, as compile-time constants.</summary>')
[void]$keys.AppendLine('public static class Keys')
[void]$keys.AppendLine('{')
foreach ($entry in $entries) {
    $doc = [System.Security.SecurityElement]::Escape($entry.en)
    [void]$keys.AppendLine("    /// <summary>English: $doc</summary>")
    [void]$keys.AppendLine("    public const string $($entry.key) = `"$($entry.key)`";")
    [void]$keys.AppendLine()
}
[void]$keys.AppendLine('    /// <summary>All keys, in declaration order.</summary>')
[void]$keys.AppendLine('    public static IReadOnlyList<string> All { get; } =')
[void]$keys.AppendLine('    [')
foreach ($entry in $entries) {
    [void]$keys.AppendLine("        $($entry.key),")
}
[void]$keys.AppendLine('    ];')
[void]$keys.AppendLine('}')

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($KeysFile, $keys.ToString(), $utf8NoBom)
Write-Host "wrote $KeysFile"
