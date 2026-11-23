# Priority baselines (0.2.21)

Snapshots now record object names, present values, units, all sixteen command
priority slots and relinquish defaults using the existing compiled engine.
Normal object browsing continues to use its existing lightweight enrichment.

## Windows workflow

1. Connect, discover and select the device in its known-good commissioning state.
2. Open Snapshots and choose Capture baseline. Capture is read-only. It reads
   priority-array and relinquish-default for every inventoried object, so
   proprietary commandable object types are not excluded by a fixed type list.
3. Review the read-error count. Unsupported properties are expected on many
   noncommandable objects; access failures on a commandable point mean that its
   priorities were not successfully observed. Failed reads never mean NULL.
4. Save baseline to JSON, or save the workspace to retain that baseline with its
   connection and watches. Record your commissioning context separately.
5. Later, load that baseline, select the same address-qualified device and choose
   Compare live. Export differences writes the displayed results to CSV.

Changed slots appear separately, for example:

| Object | Property | Baseline | Live | Change |
| --- | --- | --- | --- | --- |
| AV:1 | priority-array[8] | null | 12.5 | Changed |
| AV:1 | priority-array[16] | 3 | null | Changed |
| AV:1 | relinquish-default | 5 | 6 | Changed |

Here priority 8 was overridden and priority 16 was released. A priority change
can be reported even when the effective present value stays the same. Priority 6
is captured and compared for diagnosis; operator write restrictions are unchanged.
When available, differing slot datatypes are included in the comparison text.

## Mac preview

Capture baseline and Compare use the same snapshot/comparison owner. Save the
workspace to retain the baseline. Comparison JSON is shown in Tools & diagnostics
and can be exported there. Mac remains an unsigned, unnotarized preview that has
not been run on Mac hardware; Windows headless checks are not Mac runtime testing.

## Compatibility and limits

Existing baseline/workspace JSON remains readable and the workspace version stays
1. An old baseline has no historical priority observations: those comparisons
show **Not captured**, rather than claiming an override was added. Capture a new
known-good baseline to establish priority history. New snapshots may contain up
to 50,000 observations; 0.2.20 retains its older 30,000-observation loading guard.

Comparison requires the original device binding, including address/route identity.
An address or device-instance change can require a new baseline. A read error,
unsupported property or malformed array remains explicit; it is never expanded
into sixteen released slots. Snapshot capture is sequential and can span many
requests: it is not an atomic view of a changing controller.

Reads are batched at up to 128 objects/256 extra property selectors. Capture is
bounded to 10,000 inventory objects, 50,000 unique observations, 4 MiB of value
data and a 16 MiB serialized baseline. Cancellation or an incomplete/failed batch
retains the previous baseline. Individual property errors remain observations;
they are included in the capture error count and compared when their state changes.

This feature does not clear overrides or change device values automatically.
Review any differences before using the existing explicit write/relinquish tools.
