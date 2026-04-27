$methodMap = @{
    'AdminApiAdvancedIntegrationTests' = @('CreateTable_with_gc_rules','CreateTable_with_multiple_families')
    'AdminApiIntegrationTests' = @('Admin_created_table_is_usable_for_data','CreateTable_and_GetTable_round_trip','CreateTable_with_gc_rule','ModifyColumnFamilies_adds_family')
    'ApplyLabelTransformerTests' = @('Chain_with_two_labels_throws','Label_too_long_throws','Label_with_space_throws','Label_with_underscore_throws','Label_with_uppercase_throws')
    'ChainFilterOrderingTests' = @('Chain_single_filter_behaves_like_no_chain')
    'DeleteMutationStressTests' = @('DeleteFromColumn_empty_time_range_is_noop')
    'EdgeCaseBoundaryTests' = @('CellsPerColumnLimit_0_returns_no_cells')
    'ErrorConditionDetailTests' = @('DeleteFromFamily_nonexistent_family_throws','ReadRow_empty_range_returns_empty')
    'ErrorValidationStressTests' = @('ModifyColumnFamilies_drop_nonexistent_family_throws','RowKey_exceeds_4KiB_throws')
    'FilterLimitTests' = @('Deep_nesting_exceeding_limit_throws','Deeply_nested_condition_exceeds_limit','Deeply_nested_interleave_exceeds_limit')
    'InterleaveFilterAdvancedTests' = @('Interleave_single_branch_acts_as_filter')
    'MutateRowErrorHandlingTests' = @('DeleteFromFamily_nonexistent_throws')
    'MutationValidationTests' = @('Row_key_over_4KB_throws')
    'ReadRowsErrorAndEdgeCaseTests' = @('ReadRows_empty_range_returns_nothing')
    'ReadRowsLimitInteractionTests' = @('Reversed_limit_3','Reversed_limit_with_range')
    'ReadRowsOrderingTests' = @('Empty_scan_returns_no_rows')
    'ReadRowsRangeStressTests' = @('Empty_range_returns_nothing','Fully_unbounded_returns_all','Key_to_unbounded_end','Reversed_returns_descending_order','Reversed_with_range')
    'RowKeyRegexFilterTests' = @('RowKeyRegex_empty_pattern_matches_empty_key_only')
    'RowSetCompositionTests' = @('RowRange_from_key_to_end')
    'RowSetCompositionVariationTests' = @('Range_no_match')
}

$base = "c:\git\InMemoryEmulator.Bigtable\tests\InMemoryEmulator.Bigtable.Tests.Integration"
$totalFixed = 0
$warnings = @()

foreach ($class in $methodMap.Keys) {
    $file = "$base\$class.cs"
    $lines = Get-Content $file
    $newLines = @()
    $classFixed = 0
    
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        
        # Check if next lines contain a method we need to mark
        foreach ($method in $methodMap[$class]) {
            # Check if this line is a [Fact] or [Theory] and the next line(s) contain the method name
            if ($line -match '^\s*\[(Fact|Theory)') {
                # Look ahead up to 3 lines for the method name
                $foundMethod = $false
                for ($j = $i + 1; $j -le [Math]::Min($i + 3, $lines.Count - 1); $j++) {
                    if ($lines[$j] -match "public\s+\S+\s+${method}\s*[\(<]") {
                        $foundMethod = $true
                        break
                    }
                }
                
                if ($foundMethod) {
                    # Check if previous line already has GcpOnly trait
                    $alreadyHas = $false
                    if ($i -gt 0 -and $newLines[-1] -match 'TestTraits\.GcpOnly') {
                        $alreadyHas = $true
                    }
                    
                    if (-not $alreadyHas) {
                        $indent = ($line -match '^(\s*)') | Out-Null; $indent = $Matches[1]
                        $newLines += "${indent}[Trait(TestTraits.Target, TestTraits.GcpOnly)]"
                        $classFixed++
                        $totalFixed++
                    }
                }
            }
        }
        
        $newLines += $line
    }
    
    if ($classFixed -gt 0) {
        Set-Content $file ($newLines -join "`r`n") -NoNewline
        Write-Host "$class - Fixed $classFixed methods"
    }
}

# Verify: check each method was actually found
foreach ($class in $methodMap.Keys) {
    $file = "$base\$class.cs"
    $content = Get-Content $file -Raw
    foreach ($method in $methodMap[$class]) {
        if ($content -notmatch "GcpOnly.*\r?\n.*(?:Fact|Theory).*\r?\n.*$method") {
            $warnings += "WARNING: $class.$method may not have been fixed"
        }
    }
}

Write-Host ""
Write-Host "Total methods fixed: $totalFixed"
if ($warnings.Count -gt 0) {
    Write-Host ""
    foreach ($w in $warnings) { Write-Host $w -ForegroundColor Yellow }
}
