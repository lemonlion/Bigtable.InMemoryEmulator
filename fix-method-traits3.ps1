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

foreach ($class in $methodMap.Keys) {
    $file = "$base\$class.cs"
    $lines = [System.IO.File]::ReadAllLines($file)
    $newLines = [System.Collections.Generic.List[string]]::new()
    $classFixed = 0
    
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # For each method we need to mark, check if the NEXT line (or up to 3 lines ahead) contains it
        $shouldInsertTrait = $false
        
        if ($lines[$i] -match '^\s*\[(Fact|Theory)') {
            foreach ($method in $methodMap[$class]) {
                for ($j = $i + 1; $j -le [Math]::Min($i + 3, $lines.Count - 1); $j++) {
                    if ($lines[$j] -match "public\s+.*\s+$([regex]::Escape($method))\s*[\(<]") {
                        $shouldInsertTrait = $true
                        break
                    }
                }
                if ($shouldInsertTrait) { break }
            }
        }
        
        if ($shouldInsertTrait) {
            # Check previous line isn't already GcpOnly
            if ($newLines.Count -eq 0 -or $newLines[$newLines.Count - 1] -notmatch 'GcpOnly') {
                $indent = ''
                if ($lines[$i] -match '^(\s+)') { $indent = $Matches[1] }
                $newLines.Add("${indent}[Trait(TestTraits.Target, TestTraits.GcpOnly)]")
                $classFixed++
                $totalFixed++
            }
        }
        
        $newLines.Add($lines[$i])
    }
    
    if ($classFixed -gt 0) {
        [System.IO.File]::WriteAllLines($file, $newLines.ToArray())
        Write-Host "$class - Fixed $classFixed methods"
    }
}

Write-Host ""
Write-Host "Total methods fixed: $totalFixed"

# Verify
$warnings = 0
foreach ($class in $methodMap.Keys) {
    $file = "$base\$class.cs"
    $content = [System.IO.File]::ReadAllText($file)
    foreach ($method in $methodMap[$class]) {
        if ($content -notmatch "GcpOnly[\s\S]{0,100}$([regex]::Escape($method))") {
            Write-Host "VERIFY FAIL: $class.$method" -ForegroundColor Red
            $warnings++
        }
    }
}
if ($warnings -eq 0) { Write-Host "All methods verified!" -ForegroundColor Green }
