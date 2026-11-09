# FreeAll --- Requirements Traceability Matrix

  -------------------------------------------------------------------------------
  Requirement group Primary source    Implementation    Verification
                                      area              
  ----------------- ----------------- ----------------- -------------------------
  Windows 11        PRD, Decisions    Installer,        Release matrix
  support policy                      compatibility     
                                      service           

  WPF/.NET 10       Decisions,        Desktop project   Build and runtime tests
                    Technical Design                    

  Drive discovery   PRD, Functional   Scanner           Volume integration tests
                    Spec                                

  Deep scanning     PRD, Scanning     Scanner           Filesystem corpus
                    Design                              

  Error             Functional Spec   Scanner, DB, UI   Permission/error tests
  completeness                                          

  File              Feature Catalogue Core, Scanner     Classification tests
  classification                                        

  Explorer          UI/UX, Functional Desktop,          UI automation
                    Spec              Application       

  Application       PRD, Functional   Residue           Evidence/false-positive
  residue           Spec                                review

  Temporary files   PRD, Functional   Residue, Cleanup  Rule safety review
                    Spec                                

  Duplicates        PRD, Functional   Duplicates        Hash/byte/hard-link tests
                    Spec                                

  Recycle Bin       Cleanup Design    Cleanup           Destructive workflow
                                                        tests

  Quarantine        Cleanup Design    Cleanup           Integrity/restore tests

  Elevated helper   Architecture,     Helper            IPC/security tests
                    Security                            

  Local privacy     PRD, Security     Infrastructure    Network/privacy audit

  Updates           PRD, Deployment   Infrastructure    Signature, replay and
                                      (TD-050)          download tests

  Installer         Deployment        FreeAll.Installer Install/upgrade/uninstall
                                      (TD-049)          tests, build smoke test

  Windows storage   PRD-011, Spec §9  Application,      Allowlist and area tests
                                      Desktop (TD-048)

  Windows 11        Deployment §6     Release process   24-compatibility-matrix
  matrix

  Performance and   QA §6             Performance       Soak and cancellation
  soak                                tests (TD-052)    tests

  Accessibility     UI/UX             Desktop           Accessibility review

  Export            Feature Catalogue Reporting         Schema/format tests
  -------------------------------------------------------------------------------

Every implementation task should reference one or more requirement IDs
or feature IDs. Unmapped work must be justified as engineering support
or proposed scope.
