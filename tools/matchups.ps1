# Groups every archived match by the colours of basic land the OPPONENT revealed,
# and reports the record against each colour identity.
#
# Works on the markdown exports in %USERPROFILE%\MTGA_PlayByPlay\out\text (or pass
# -TextDir). Windows PowerShell 5.1 compatible.
#
# How it reads a transcript:
#   - A bare "- CardName" line only ever appears in the "Seen from the opponent"
#     section — your own deck's lines carry counts ("30× Plains") — so matching
#     "^- Island$" asks "did the opponent reveal an Island", never "did I play one".
#   - The \r? before each $ matters: the files carry Windows line endings and .NET's
#     multiline $ anchors only before \n, so without it every pattern silently
#     misses and everything lands in one bucket.
#
# What the numbers mean, and what they do not:
#   - "Colours revealed" is a proxy for the deck's identity, not the identity
#     itself. A two-colour deck on an all-dual manabase reads as fewer colours;
#     ramp decks reveal generously. The commander-keyed Against table on the index
#     is the precise version — this script is the quick terminal cut.
#   - Buckets are disjoint (each match lands in exactly one row), so the Games
#     column reconciles against your overall record.
#   - The "No basics seen" row skews toward your fast wins: a game that ends early
#     is a game where the opponent never got to reveal lands. Don't quote its
#     win rate as a matchup.

param(
    [string]$TextDir = "$env:USERPROFILE\MTGA_PlayByPlay\out\text"
)

$names = @{
    'W' = 'Mono-White'; 'U' = 'Mono-Blue'; 'B' = 'Mono-Black'; 'R' = 'Mono-Red'; 'G' = 'Mono-Green'
    'WU' = 'Azorius'; 'UB' = 'Dimir'; 'BR' = 'Rakdos'; 'RG' = 'Gruul'; 'WG' = 'Selesnya'
    'WB' = 'Orzhov'; 'UR' = 'Izzet'; 'BG' = 'Golgari'; 'WR' = 'Boros'; 'UG' = 'Simic'
    'WUR' = 'Jeskai'; 'WUB' = 'Esper'; 'UBR' = 'Grixis'; 'BRG' = 'Jund'; 'WRG' = 'Naya'
    'WUG' = 'Bant'; 'WBG' = 'Abzan'; 'URG' = 'Temur'; 'WBR' = 'Mardu'; 'UBG' = 'Sultai'
    'WUBRG' = 'Five-Color'; '' = 'No basics seen'
}

$tally = @{}
foreach ($f in Get-ChildItem (Join-Path $TextDir '*.md')) {
    $text = Get-Content $f -Raw
    $sig = ''
    if ($text -match '(?m)^- (Snow-Covered )?Plains\r?$') { $sig += 'W' }
    if ($text -match '(?m)^- (Snow-Covered )?Island\r?$') { $sig += 'U' }
    if ($text -match '(?m)^- (Snow-Covered )?Swamp\r?$') { $sig += 'B' }
    if ($text -match '(?m)^- (Snow-Covered )?Mountain\r?$') { $sig += 'R' }
    if ($text -match '(?m)^- (Snow-Covered )?Forest\r?$') { $sig += 'G' }

    # Line 3 of every export is the subtitle: event · date · result · length.
    $sub = (Get-Content $f -TotalCount 3)[2]
    $result = if ($sub -match 'Won') { 'Won' } elseif ($sub -match 'Lost') { 'Lost' }
    elseif ($sub -match 'Drew') { 'Drew' } else { 'Unfinished' }

    $clan = if ($names.ContainsKey($sig)) { $names[$sig] } else { "4c ($sig)" }
    if (-not $tally[$clan]) { $tally[$clan] = @{ Won = 0; Lost = 0; Drew = 0; Unfinished = 0 } }
    $tally[$clan][$result]++
}

$tally.Keys | ForEach-Object {
    $t = $tally[$_]; $played = $t.Won + $t.Lost
    [pscustomobject]@{
        TheirColors = $_
        Won         = $t.Won
        Lost        = $t.Lost
        Games       = $played
        WinRate     = if ($played) { '{0:P1}' -f ($t.Won / $played) } else { '' }
    }
} | Sort-Object Games -Descending | Format-Table -AutoSize
