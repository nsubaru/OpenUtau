get_filename_component(ceiling "${SOURCE_DIR}" DIRECTORY)
execute_process(COMMAND "${CMAKE_COMMAND}" -E env "GIT_CEILING_DIRECTORIES=${ceiling}"
    "${GIT_EXECUTABLE}" apply --reverse --check --ignore-space-change "${PATCH_FILE}"
    WORKING_DIRECTORY "${SOURCE_DIR}" RESULT_VARIABLE already_applied OUTPUT_QUIET ERROR_QUIET)
if(already_applied EQUAL 0)
    return()
endif()
execute_process(COMMAND "${CMAKE_COMMAND}" -E env "GIT_CEILING_DIRECTORIES=${ceiling}"
    "${GIT_EXECUTABLE}" apply --ignore-space-change "${PATCH_FILE}"
    WORKING_DIRECTORY "${SOURCE_DIR}" RESULT_VARIABLE applied)
if(NOT applied EQUAL 0)
    message(FATAL_ERROR "Pinned native dependency patch failed: ${PATCH_FILE}")
endif()
