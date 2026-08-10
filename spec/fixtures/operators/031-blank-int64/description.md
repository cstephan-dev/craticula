Proves AR-6's `blank` operator on a non-text (`int64`) field: SEM-2 pins `blank` as `IS NULL` alone for non-text types — the text-specific `= ''` clause must NOT be applied here.
