// A compilable document that declares nothing. `csx outline` on it must print the header and
// exit 0 -- an answered query about a file with no symbols, the way `diag` treats a clean
// file, not the exit 1 that `refs` and `def` use for a lookup that found nothing. Every other
// fixture file declares something, so this is the only thing that reaches that branch.
