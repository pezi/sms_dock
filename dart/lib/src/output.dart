// --------------------------------------------------------------------------
// Filename: output.dart
//
// Description: Where the package writes warnings: standard error, or a sink
//   that a zone has put in its place.
// --------------------------------------------------------------------------

/// Where the package writes warnings.
library;

import 'dart:async';
import 'dart:io';

/// Zone value that replaces standard error for the code of this package.
final Object errorSinkKey = Object();

/// Standard error, or the sink a zone has put in its place.
StringSink get errorSink =>
    (Zone.current[errorSinkKey] as StringSink?) ?? stderr;
