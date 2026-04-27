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
    $content = Get-Content $file -Raw
    $classFixed = 0
    
    foreach ($method in $methodMap[$class]) {
        # Use regex to find [Fact] or [Theory...] line before the method
        # Pattern: (whitespace)[Fact] or [Theory...] CRLF (whitespace)public ... method_name
        $escapedMethod = [regex]::Escape($method)
        $pattern = "(?m)([ \t]*)(\[(?:Fact|Theory[^\]]*)\]\r?\n[ \t]*public\s+\S+\s+${escapedMethod})"
        
        if ($content -match $pattern) {
            $indent = $Matches[1]
            $traitLine = "${indent}[Trait(TestTraits.Target, TestTraits.GcpOnly)]`r`n"
            $content = [regex]::Replace($content, $pattern, "${traitLine}`$1`$2", [System.Text.RegularExpressions.RegexOptions]::Multiline)
            $classFixed++
            $totalFixed++
        } else {
            Write-Host "WARNING: Could not find pattern for $class.$method" -ForegroundColor Yellow
        }
    }
    
    if ($classFixed -gt 0) {
        Set-Content $file $content -NoNewline
        Write-Host "$class - Fixed $classFixed methods"
    }
}

Write-Host ""
Write-Host "Total methods fixed: $totalFixed"
